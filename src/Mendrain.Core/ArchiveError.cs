using System.Security.Cryptography;
using Mendrain.Core.Safety;

namespace Mendrain.Core;

public enum ArchiveErrorKind
{
    PasswordRequired,
    /// <summary>The password was rejected, or (for formats that cannot tell) the encrypted data is damaged.</summary>
    WrongPassword,
    NotAnArchive,
    UnsupportedFormat,
    Corrupt,
    MissingVolumes,
    NotFound,
    AccessDenied,
    DiskFull,
    DestinationExists,
    InvalidInput,
    Cancelled,
    IO,
    Unknown,
}

/// <summary>A failure with a user-readable message. All core operations throw this type.</summary>
public sealed class ArchiveError : Exception
{
    public ArchiveError(ArchiveErrorKind kind, string message, Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
    }

    public ArchiveErrorKind Kind { get; }

    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorDiskFull = unchecked((int)0x80070070);

    /// <summary>
    /// Maps library and file-system exceptions to a kind and a message a user can act on.
    /// </summary>
    /// <param name="passwordSupplied">A password was given for this attempt.</param>
    /// <param name="knownEncrypted">The archive is known to contain encrypted data.</param>
    /// <param name="context">Optional entry or file name the failure relates to.</param>
    public static ArchiveError From(Exception ex, bool passwordSupplied = false, bool knownEncrypted = false, string? context = null)
    {
        if (ex is ArchiveError existing)
            return existing;
        if (ex is AggregateException { InnerExceptions.Count: 1 } agg)
            return From(agg.InnerExceptions[0], passwordSupplied, knownEncrypted, context);

        var where = context is null ? "" : $" ({context})";
        var typeName = ex.GetType().Name;
        var message = ex.Message ?? "";

        switch (ex)
        {
            case OperationCanceledException:
            case SharpCompress.Common.ReaderCancelledException:
                return new(ArchiveErrorKind.Cancelled, "The operation was cancelled.", ex);

            case UnsafePathException:
                return new(ArchiveErrorKind.InvalidInput, message, ex);

            case SharpCompress.Common.CryptographicException:
                if (!passwordSupplied || message.Contains("no password", StringComparison.OrdinalIgnoreCase))
                    return new(ArchiveErrorKind.PasswordRequired, "This archive is encrypted. Enter its password to continue.", ex);
                return new(ArchiveErrorKind.WrongPassword, "The password is incorrect.", ex);

            case SharpCompress.Common.IncompleteArchiveException:
            case SharpCompress.Common.MultiVolumeExtractionException:
            case SharpCompress.Common.MultipartStreamRequiredException:
                return new(ArchiveErrorKind.MissingVolumes,
                    "The archive is incomplete. If it is split into parts, make sure every part is in the same folder.", ex);
        }

        // With a password in play, several formats report a bad key as a generic data error
        // (7z AES has no password check value; AES-ZIP fails while opening). Say so honestly.
        if (passwordSupplied && knownEncrypted &&
            (ex is SharpCompress.Common.SharpCompressException or InvalidDataException or EndOfStreamException
                or ChecksumMismatchException || typeName == "DataErrorException"))
        {
            return new(ArchiveErrorKind.WrongPassword, "The password is incorrect, or the archive is damaged.", ex);
        }

        switch (ex)
        {
            case SharpCompress.Common.ArchiveOperationException when message.Contains("Cannot determine", StringComparison.OrdinalIgnoreCase):
            case SharpCompress.Common.ArchiveException when message.Contains("locate", StringComparison.OrdinalIgnoreCase):
                return new(ArchiveErrorKind.NotAnArchive, "This file is not a supported archive, or it is too damaged to open.", ex);

            case ChecksumMismatchException:
                return new(ArchiveErrorKind.Corrupt, $"The archive is damaged: {ex.Message}", ex);

            case NotSupportedException:
                return new(ArchiveErrorKind.UnsupportedFormat, $"This archive uses a feature Mendrain cannot read{where}: {message}", ex);

            case SharpCompress.Common.SharpCompressException or InvalidDataException or EndOfStreamException:
                if (message.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
                    return new(ArchiveErrorKind.UnsupportedFormat, $"This archive uses a compression method Mendrain cannot read{where}.", ex);
                return new(ArchiveErrorKind.Corrupt, $"The archive is damaged and could not be read{where}.", ex);

            case CryptographicException:
                return new(ArchiveErrorKind.Corrupt, $"The archive's encrypted data could not be decoded{where}.", ex);

            case FileNotFoundException or DirectoryNotFoundException:
                return new(ArchiveErrorKind.NotFound, $"A file or folder could not be found{where}: {message}", ex);

            case UnauthorizedAccessException:
                return new(ArchiveErrorKind.AccessDenied, $"Access was denied{where}. Check the folder's permissions or choose another location.", ex);

            case PathTooLongException:
                return new(ArchiveErrorKind.IO, $"A path is too long for Windows{where}.", ex);

            case IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull:
                return new(ArchiveErrorKind.DiskFull, "There is not enough free space on the destination drive.", ex);

            case IOException:
                return new(ArchiveErrorKind.IO, $"A file operation failed{where}: {message}", ex);
        }

        if (typeName == "DataErrorException")
            return new(ArchiveErrorKind.Corrupt, $"The archive is damaged (data error){where}.", ex);

        return new(ArchiveErrorKind.Unknown, $"Unexpected error{where}: {message}", ex);
    }
}

/// <summary>Raised when extracted data does not match the size or CRC stored in the archive.</summary>
public sealed class ChecksumMismatchException(string message) : IOException(message);
