using System.Runtime.InteropServices;
using System.Security;

namespace SqlBackupManager.Utilities;

/// <summary>
/// Validates and normalizes file system paths. Checks directory accessibility
/// and ensures paths are well-formed before use.
/// </summary>
public static class PathValidator
{
    /// <summary>
    /// Validates that the specified directory path exists and is accessible.
    /// </summary>
    /// <param name="path">Directory path to validate.</param>
    /// <param name="label">Human-readable label for error messages (e.g., "Log directory").</param>
    /// <param name="createIfMissing">When true, creates the directory if it doesn't exist.</param>
    /// <returns>True if the directory exists and is accessible.</returns>
    public static bool ValidateDirectory(
        string path,
        string label,
        bool createIfMissing = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine($"[CONFIG ERROR] {label} is not configured (empty path).");
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);

            if (!Directory.Exists(fullPath))
            {
                if (createIfMissing)
                {
                    Directory.CreateDirectory(fullPath);
                    return true;
                }

                Console.Error.WriteLine($"[CONFIG ERROR] {label} does not exist: {fullPath}");
                return false;
            }

            // Verify read/write access by attempting to enumerate (read) the directory
            _ = Directory.EnumerateFileSystemEntries(fullPath).Take(0).Count();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[CONFIG ERROR] Access denied to {label}: {path}");
            return false;
        }
        catch (SecurityException)
        {
            Console.Error.WriteLine($"[CONFIG ERROR] Security restriction on {label}: {path}");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CONFIG ERROR] {label} is invalid: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Validates that a UNC share path is well-formed and accessible for reading.
    /// Used for BackupDirectoryFromApp paths.
    /// </summary>
    public static bool ValidateUncShare(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine($"[CONFIG ERROR] {label} is not configured (empty path).");
            return false;
        }

        // Check UNC format: \\server\share...
        if (!path.StartsWith(@"\\"))
        {
            Console.Error.WriteLine($"[CONFIG ERROR] {label} is not a UNC path: {path}");
            return false;
        }

        try
        {
            if (!Directory.Exists(path))
            {
                Console.Error.WriteLine($"[CONFIG ERROR] {label} UNC share is not accessible: {path}");
                return false;
            }

            _ = Directory.EnumerateFileSystemEntries(path).Take(0).Count();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[CONFIG ERROR] Access denied to {label}: {path}");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CONFIG ERROR] {label} validation failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Validates that a local path is well-formed and accessible for writing.
    /// Optionally creates the directory if it doesn't exist.
    /// </summary>
    public static bool EnsureWritableDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine($"[CONFIG ERROR] {label} is not configured (empty path).");
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);

            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }

            // Verify write access by creating and deleting a temp marker
            var testFile = Path.Combine(fullPath, $".write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[CONFIG ERROR] Write access denied to {label}: {path}");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CONFIG ERROR] {label} is not writable: {ex.Message}");
            return false;
        }
    }
}
