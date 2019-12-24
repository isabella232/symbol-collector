using System;
using System.Collections;
using System.IO;
using System.Threading;

namespace SymbolCollector.Core
{
    public class ClientMetrics
    {
        private int _filesProcessedCount;
        private int _batchesProcessedCount;
        private int _jobsInFlightCount;
        private int _failedToUploadCount;
        private int _successfullyUploadCount;
        private int _machOFileFoundCount;
        private int _elfFileFoundCount;
        private int _fatMachOFileFoundCount;
        private long _uploadedBytesCount;
        private int _directoryUnauthorizedAccessCount;
        private int _directoryDoesNotExistCount;

        public DateTimeOffset StartedTime { get; } = DateTimeOffset.Now;

        public long FilesProcessedCount => _filesProcessedCount;

        public long BatchesProcessedCount => _batchesProcessedCount;

        public long JobsInFlightCount => _jobsInFlightCount;

        public long FailedToUploadCount => _failedToUploadCount;

        public long SuccessfullyUploadCount => _successfullyUploadCount;

        public long MachOFileFoundCount => _machOFileFoundCount;

        public long ElfFileFoundCount => _elfFileFoundCount;

        public int FatMachOFileFoundCount => _fatMachOFileFoundCount;

        public long UploadedBytesCount => _uploadedBytesCount;

        public int DirectoryUnauthorizedAccessCount => _directoryUnauthorizedAccessCount;
        public int DirectoryDoesNotExistCount => _directoryDoesNotExistCount;

        public void FileProcessed() => Interlocked.Increment(ref _filesProcessedCount);
        public void BatchProcessed() => Interlocked.Increment(ref _batchesProcessedCount);
        public void MachOFileFound() => Interlocked.Increment(ref _machOFileFoundCount);
        public void ElfFileFound() => Interlocked.Increment(ref _elfFileFoundCount);
        public void FatMachOFileFound() => Interlocked.Increment(ref _fatMachOFileFoundCount);
        public void FailedToUpload() => Interlocked.Increment(ref _failedToUploadCount);
        public void SuccessfulUpload() => Interlocked.Increment(ref _successfullyUploadCount);
        public void JobsInFlightRemove(int tasksCount) => Interlocked.Add(ref _jobsInFlightCount, -tasksCount);
        public void JobsInFlightAdd(int tasksCount) => Interlocked.Add(ref _jobsInFlightCount, tasksCount);
        public void UploadedBytesAdd(long bytes) => Interlocked.Add(ref _uploadedBytesCount, bytes);
        public void DirectoryUnauthorizedAccess() => Interlocked.Increment(ref _directoryUnauthorizedAccessCount);
        public void DirectoryDoesNotExist() => Interlocked.Increment(ref _directoryDoesNotExistCount);
        public TimeSpan RanFor => DateTimeOffset.Now - StartedTime;

        public void Write(IDictionary data)
        {
            data["Started at"] = StartedTime;
            data["Ran for"] = RanFor;
            data["File Processed"] = FilesProcessedCount;
            // TODO: Fix PII stripping, Sentry is deleting the field for containing "Unauthorized"
            data["Directory Unautnorized"] = DirectoryUnauthorizedAccessCount;
            data["Directory DoesNotExist"] = DirectoryDoesNotExistCount;
            data["Batches completed"] = BatchesProcessedCount;
            data["Jobs in flight"] = JobsInFlightCount;
            data["Failed to upload"] = FailedToUploadCount;
            data["Successfully uploaded"] = SuccessfullyUploadCount;
            data["Uploaded bytes"] = UploadedBytesCount;
            data["ELF files loaded"] = ElfFileFoundCount;
            data["Mach-O files loaded"] = MachOFileFoundCount;
            data["Fat Mach-O files loaded"] = FatMachOFileFoundCount;
        }

        public void Write(TextWriter writer)
        {
            writer.WriteLine();
            writer.Write("Started at:\t\t\t\t");
            writer.WriteLine(StartedTime);
            writer.Write("Ran for:\t\t\t\t");
            writer.WriteLine(RanFor);
            writer.Write("File Processed:\t\t\t\t");
            writer.WriteLine(FilesProcessedCount);
            writer.Write("Directory UnauthorizedAccess:\t\t");
            writer.WriteLine(DirectoryUnauthorizedAccessCount);
            writer.Write("Directory DoesNotExist:\t\t\t");
            writer.WriteLine(DirectoryDoesNotExistCount);
            writer.Write("Batches completed:\t\t\t");
            writer.WriteLine(BatchesProcessedCount);
            writer.Write("Job in flight:\t\t\t\t");
            writer.WriteLine(JobsInFlightCount);
            writer.Write("Failed to upload:\t\t\t");
            writer.WriteLine(FailedToUploadCount);
            writer.Write("Successfully uploaded:\t\t\t");
            writer.WriteLine(SuccessfullyUploadCount);
            writer.Write("Uploaded bytes:\t\t\t\t");
            writer.WriteLine(UploadedBytesCount);
            writer.Write("ELF files loaded:\t\t\t");
            writer.WriteLine(ElfFileFoundCount);
            writer.Write("Mach-O files loaded:\t\t\t");
            writer.WriteLine(MachOFileFoundCount);
            writer.Write("Fat Mach-O files loaded:\t\t");
            writer.WriteLine(FatMachOFileFoundCount);
        }
    }
}
