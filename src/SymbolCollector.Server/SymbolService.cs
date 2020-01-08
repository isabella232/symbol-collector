using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SymbolCollector.Core;
using SymbolCollector.Server.Models;

namespace SymbolCollector.Server
{
    public interface ISymbolService
    {
        Task Start(Guid batchId, string friendlyName, BatchType batchType, CancellationToken token);
        Task<SymbolUploadBatch?> GetBatch(Guid batchId, CancellationToken token);
        Task<SymbolMetadata?> GetSymbol(string debugId, CancellationToken token);
        Task Relate(Guid batchId, SymbolMetadata symbolMetadata, CancellationToken token);
        Task Finish(Guid batchId, IClientMetrics? clientMetrics, CancellationToken token);
        Task<StoreResult> Store(Guid batchId, string fileName, Stream stream, CancellationToken token);
    }

    public enum StoreResult
    {
        Invalid,
        Created,
        AlreadyExisted
    }

    public class SymbolServiceOptions
    {
        public string SymsorterPath { get; set; } = null!; // Either bound via configuration or thrown early

        private string _baseWorkingPath = null!; // Either bound via configuration or thrown early

        public string BaseWorkingPath
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_baseWorkingPath))
                {
                    return _baseWorkingPath;
                }

                if (Directory.Exists(_baseWorkingPath))
                {
                    return _baseWorkingPath;
                }

                var info = Directory.CreateDirectory(_baseWorkingPath);
                if (!info.Exists)
                {
                    throw new InvalidOperationException(
                        "Base path configured does not exist and could not be created.");
                }

                return _baseWorkingPath;
            }
            set => _baseWorkingPath = value;
        }
    }

    internal class InMemorySymbolService : ISymbolService, IDisposable
    {
        private readonly ObjectFileParser _parser;
        private readonly IBatchFinalizer _batchFinalizer;
        private readonly SymbolServiceOptions _options;
        private readonly ILogger<InMemorySymbolService> _logger;
        private readonly Random _random = new Random();

        private readonly ConcurrentDictionary<Guid, SymbolUploadBatch> _batches =
            new ConcurrentDictionary<Guid, SymbolUploadBatch>();

        private readonly string _donePath;
        private readonly string _processingPath;
        private readonly string _conflictPath;

        public InMemorySymbolService(
            ObjectFileParser parser,
            IBatchFinalizer batchFinalizer,
            IOptions<SymbolServiceOptions> options,
            ILogger<InMemorySymbolService> logger)
        {
            _parser = parser;
            _batchFinalizer = batchFinalizer;
            _options = options.Value;
            _logger = logger;

            _donePath = Path.Combine(_options.BaseWorkingPath, "done");
            _processingPath = Path.Combine(_options.BaseWorkingPath, "processing");
            _conflictPath = Path.Combine(_options.BaseWorkingPath, "conflict");
            Directory.CreateDirectory(_donePath);
            Directory.CreateDirectory(_processingPath);
            Directory.CreateDirectory(_conflictPath);
        }

        public Task Start(Guid batchId, string friendlyName, BatchType batchType, CancellationToken token)
        {
            if (_batches.ContainsKey(batchId))
            {
                throw new ArgumentException($"Batch Id {batchId} was already used.");
            }

            _batches[batchId] = new SymbolUploadBatch(batchId, friendlyName, batchType);
            var batchIdString = batchId.ToString();
            var processingDir = Path.Combine(_processingPath, batchIdString);
            Directory.CreateDirectory(processingDir);

            _logger.LogInformation("Started batch {batchId} with friendly name {friendlyName} and type {batchType}",
                batchIdString, friendlyName, batchType);

            return Task.CompletedTask;
        }

        public Task<SymbolMetadata?> GetSymbol(string debugId, CancellationToken token)
        {
            var symbol =
                _batches.Values.SelectMany(b => b.Symbols)
                    .Select(s => s.Value)
                    .FirstOrDefault(s => s.DebugId == debugId);

            return Task.FromResult((SymbolMetadata?)symbol);
        }

        public async Task<StoreResult> Store(Guid batchId, string fileName, Stream stream, CancellationToken token)
        {
            var batch = await GetOpenBatch(batchId, token);

            // TODO: Until parser supports Stream instead of file path, we write the file to TMP before we can validate it.
            var destination = Path.Combine(
                _processingPath,
                batchId.ToString(),
                // To avoid files with conflicting name from the same batch
                _random.Next().ToString(CultureInfo.InvariantCulture),
                fileName);
            var tempDestination = Path.Combine(Path.GetTempPath(), destination);
            Directory.CreateDirectory(Path.GetDirectoryName(tempDestination));

            await using (var file = File.OpenWrite(tempDestination))
            {
                await stream.CopyToAsync(file, token);
            }

            if (!_parser.TryParse(tempDestination, out var fileResult) || fileResult is null)
            {
                _logger.LogDebug("Failed parsing {file}.", Path.GetFileName(tempDestination));
                File.Delete(tempDestination);
                return StoreResult.Invalid;
            }

            _logger.LogInformation("Parsed file with {buildId}", fileResult.BuildId);
            var symbol = await GetSymbol(fileResult.BuildId, token);
            if (symbol is {})
            {
                if (fileResult.Hash is {}
                    && symbol.Hash is {}
                    && string.CompareOrdinal(fileResult.Hash, symbol.Hash) != 0)
                {
                    // TODO: Unlikely case a debugId on un-matching file hash (modified file?)
                    // TODO: Store the file for debugging, raise a Sentry event attachments
                    var conflictDestination = Path.Combine(
                        _conflictPath,
                        batchId.ToString(),
                        // To avoid files with conflicting name from the same batch
                        _random.Next().ToString(CultureInfo.InvariantCulture),
                        fileName);

                    using (_logger.BeginScope(new Dictionary<string, string>()
                    {
                        {"existing-file-hash", symbol.Hash},
                        {"existing-file-name", symbol.Name},
                        {"staging-location", conflictDestination},
                        {"new-file-hash", fileResult.Hash},
                        {"new-file-name", Path.GetFileName(fileResult.Path)}
                    }))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(conflictDestination));
                        _logger.LogError(
                            "File with the same debug id and un-matching hashes. File stored at: {path}",
                            conflictDestination);
                        File.Move(tempDestination, conflictDestination);
                    }
                }
                else
                {
                    if (symbol.BatchIds.Any(b => b == batchId))
                    {
                        _logger.LogDebug(
                            "Client uploading the same file {fileName} as part of the same batch {batchId}",
                            fileName, batchId);
                    }
                    else
                    {
                        await Relate(batchId, symbol, token);
                    }
                }

                _logger.LogDebug("Symbol {debugId} already exists.", symbol.DebugId);

                return StoreResult.AlreadyExisted;
            }

            var metadata = new SymbolMetadata(
                fileResult.BuildId,
                fileResult.Hash,
                destination,
                fileResult.ObjectFileType,
                fileName,
                fileResult.Architecture,
                fileResult.FileFormat,
                new HashSet<Guid> {batchId});

            batch.Symbols[metadata.DebugId] = metadata;

            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Move(tempDestination, destination);

            _logger.LogDebug("File {fileName} created.", metadata.Name);

            return StoreResult.Created;
        }

        public async Task Relate(Guid batchId, SymbolMetadata symbolMetadata, CancellationToken token)
        {
            var batch = await GetOpenBatch(batchId, token);
            batch.Symbols[symbolMetadata.DebugId] = symbolMetadata;
            symbolMetadata.BatchIds.Add(batchId);

            _logger.LogDebug("Symbol {debugId} is now related to batch {batchId}.",
                symbolMetadata.DebugId, batchId);
        }

        public Task<SymbolUploadBatch?> GetBatch(Guid batchId, CancellationToken token) =>
            _batches.TryGetValue(batchId, out var batch)
                ? Task.FromResult<SymbolUploadBatch?>(batch)
                : Task.FromResult<SymbolUploadBatch?>(null);

        public async Task Finish(Guid batchId, IClientMetrics? clientMetrics, CancellationToken token)
        {
            var batch = await GetOpenBatch(batchId, token);

            // TODO: Validate client metrics against data collected (recon)
            batch.ClientMetrics = clientMetrics;
            batch.Close();

            var processingLocation = Path.Combine(_processingPath, batchId.ToString());

            var destination = Path.Combine(_donePath, batchId.ToString());
            foreach (var symbol in batch.Symbols.Values)
            {
                symbol.Path = symbol.Path.Replace(processingLocation, destination);
            }

            await using (var file = File.OpenWrite(Path.Combine(processingLocation, "metadata.json")))
            {
                await JsonSerializer.SerializeAsync(
                    file,
                    batch,
                    cancellationToken: token,
                    options: new JsonSerializerOptions {WriteIndented = true});
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Directory.Move(processingLocation, destination);

            _logger.LogInformation("Batch {batchId} is now closed at {location}.",
                batchId, destination);

            await _batchFinalizer.CloseBatch(destination, batch, token);
        }


        private async Task<SymbolUploadBatch> GetOpenBatch(Guid batchId, CancellationToken token)
        {
            var batch = await GetBatch(batchId, token);
            if (batch is null)
            {
                throw new InvalidOperationException($"Batch '{batchId}' was not found.");
            }

            if (batch.IsClosed)
            {
                throw new InvalidOperationException($"Batch '{batchId}' was already closed at {batch.EndTime}.");
            }

            return batch;
        }

        public void Dispose() => (_batchFinalizer as IDisposable)?.Dispose();
    }
}