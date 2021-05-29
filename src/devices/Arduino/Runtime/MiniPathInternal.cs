﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Iot.Device.Arduino.Runtime
{
    // This uses the unix implementation
    [ArduinoReplacement("System.IO.PathInternal", "System.Private.CoreLib.dll", true, IncludingPrivates = true)]
    internal class MiniPathInternal
    {
        internal const char DirectorySeparatorChar = '/';
        internal const char AltDirectorySeparatorChar = '/';
        internal const char VolumeSeparatorChar = '/';
        internal const char PathSeparator = ':';
        internal const string DirectorySeparatorCharAsString = "/";
        internal const string ParentDirectoryPrefix = @"../";

        internal static int GetRootLength(ReadOnlySpan<char> path)
        {
            return path.Length > 0 && IsDirectorySeparator(path[0]) ? 1 : 0;
        }

        internal static bool IsDirectorySeparator(char c)
        {
            // The alternate directory separator char is the same as the directory separator,
            // so we only need to check one.
            return c == DirectorySeparatorChar;
        }

        /// <summary>
        /// Normalize separators in the given path. Compresses forward slash runs.
        /// </summary>
        internal static string? NormalizeDirectorySeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            // Make a pass to see if we need to normalize so we can potentially skip allocating
            bool normalized = true;

            for (int i = 0; i < path.Length; i++)
            {
                if (IsDirectorySeparator(path[i])
                    && (i + 1 < path.Length && IsDirectorySeparator(path[i + 1])))
                {
                    normalized = false;
                    break;
                }
            }

            if (normalized)
            {
                return path;
            }

            StringBuilder builder = new StringBuilder(path.Length);

            for (int i = 0; i < path.Length; i++)
            {
                char current = path[i];

                // Skip if we have another separator following
                if (IsDirectorySeparator(current)
                    && (i + 1 < path.Length && IsDirectorySeparator(path[i + 1])))
                {
                    continue;
                }

                builder.Append(current);
            }

            return builder.ToString();
        }

        internal static bool IsPartiallyQualified(ReadOnlySpan<char> path)
        {
            // This is much simpler than Windows where paths can be rooted, but not fully qualified (such as Drive Relative)
            // As long as the path is rooted in Unix it doesn't use the current directory and therefore is fully qualified.
            return !Path.IsPathRooted(path);
        }

        /// <summary>
        /// Try to remove relative segments from the given path (without combining with a root).
        /// </summary>
        /// <param name="path">Input path</param>
        /// <param name="rootLength">The length of the root of the given path</param>
        internal static string RemoveRelativeSegments(string path, int rootLength)
        {
            var sb = new StringBuilder();

            if (RemoveRelativeSegments(path.AsSpan(), rootLength, ref sb))
            {
                path = sb.ToString();
            }

            return path;
        }

        /// <summary>
        /// Try to remove relative segments from the given path (without combining with a root).
        /// </summary>
        /// <param name="path">Input path</param>
        /// <param name="rootLength">The length of the root of the given path</param>
        /// <param name="sb">String builder that will store the result</param>
        /// <returns>"true" if the path was modified</returns>
        internal static bool RemoveRelativeSegments(ReadOnlySpan<char> path, int rootLength, ref StringBuilder sb)
        {
            bool flippedSeparator = false;

            int skip = rootLength;
            // We treat "\.." , "\." and "\\" as a relative segment. We want to collapse the first separator past the root presuming
            // the root actually ends in a separator. Otherwise the first segment for RemoveRelativeSegments
            // in cases like "\\?\C:\.\" and "\\?\C:\..\", the first segment after the root will be ".\" and "..\" which is not considered as a relative segment and hence not be removed.
            if (IsDirectorySeparator(path[skip - 1]))
            {
                skip--;
            }

            // Remove "//", "/./", and "/../" from the path by copying each character to the output,
            // except the ones we're removing, such that the builder contains the normalized path
            // at the end.
            if (skip > 0)
            {
                sb.Append(path.Slice(0, skip));
            }

            for (int i = skip; i < path.Length; i++)
            {
                char c = path[i];

                if (IsDirectorySeparator(c) && i + 1 < path.Length)
                {
                    // Skip this character if it's a directory separator and if the next character is, too,
                    // e.g. "parent//child" => "parent/child"
                    if (IsDirectorySeparator(path[i + 1]))
                    {
                        continue;
                    }

                    // Skip this character and the next if it's referring to the current directory,
                    // e.g. "parent/./child" => "parent/child"
                    if ((i + 2 == path.Length || IsDirectorySeparator(path[i + 2])) &&
                        path[i + 1] == '.')
                    {
                        i++;
                        continue;
                    }

                    // Skip this character and the next two if it's referring to the parent directory,
                    // e.g. "parent/child/../grandchild" => "parent/grandchild"
                    if (i + 2 < path.Length &&
                        (i + 3 == path.Length || IsDirectorySeparator(path[i + 3])) &&
                        path[i + 1] == '.' && path[i + 2] == '.')
                    {
                        // Unwind back to the last slash (and if there isn't one, clear out everything).
                        int s;
                        for (s = sb.Length - 1; s >= skip; s--)
                        {
                            if (IsDirectorySeparator(sb[s]))
                            {
                                sb.Length = (i + 3 >= path.Length && s == skip) ? s + 1 : s; // to avoid removing the complete "\tmp\" segment in cases like \\?\C:\tmp\..\, C:\tmp\..
                                break;
                            }
                        }

                        if (s < skip)
                        {
                            sb.Length = skip;
                        }

                        i += 2;
                        continue;
                    }
                }

                // Normalize the directory separator if needed
                if (c != DirectorySeparatorChar && c == AltDirectorySeparatorChar)
                {
                    c = DirectorySeparatorChar;
                    flippedSeparator = true;
                }

                sb.Append(c);
            }

            // If we haven't changed the source path, return the original
            if (!flippedSeparator && sb.Length == path.Length)
            {
                return false;
            }

            // We may have eaten the trailing separator from the root when we started and not replaced it
            if (skip != rootLength && sb.Length < rootLength)
            {
                sb.Append(path[rootLength - 1]);
            }

            return true;
        }

        internal static string? TrimEndingDirectorySeparator(string? path)
        {
            if (path == null)
            {
                return path;
            }

            return path.EndsWith('/') && path.Length > 1 ? path!.Substring(0, path.Length - 1) : path;
        }

        internal static ReadOnlySpan<Char> TrimEndingDirectorySeparator(ReadOnlySpan<Char> path)
        {
            if (path.EndsWith("/") && path.Length > 1)
            {
                return path.Slice(0, path.Length - 1);
            }

            return path;
        }

        internal static bool IsExtended(ReadOnlySpan<char> path)
        {
            // Extended paths are windows-only
            return false;
        }
    }

    [ArduinoReplacement("System.IO.PathInternal", "System.IO.FileSystem.dll", false, typeof(System.IO.File), IncludingPrivates = true)]
    internal sealed class MiniPathInternal_File
    {
        [ArduinoImplementation]
        public static bool GetIsCaseSensitive()
        {
            return true;
        }

        [ArduinoImplementation]
        public static bool IsEffectivelyEmpty(ReadOnlySpan<char> path)
        {
            return path.IsEmpty;
        }

        [ArduinoImplementation]
        public static int GetRootLength(ReadOnlySpan<char> path)
        {
            return path.Length > 0 && IsDirectorySeparator(path[0]) ? 1 : 0;
        }

        [ArduinoImplementation]
        public static bool EndsInDirectorySeparator(ReadOnlySpan<char> path)
            => path.Length > 0 && IsDirectorySeparator(path[path.Length - 1]);

        public static ReadOnlySpan<char> TrimEndingDirectorySeparator(ReadOnlySpan<char> path) =>
            EndsInDirectorySeparator(path) && !IsRoot(path) ?
                path.Slice(0, path.Length - 1) :
                path;

        [ArduinoImplementation]
        public static bool IsRoot(ReadOnlySpan<char> path)
            => path.Length == GetRootLength(path);

        [ArduinoImplementation]
        public static bool IsDirectorySeparator(char c)
        {
            // The alternate directory separator char is the same as the directory separator,
            // so we only need to check one.
            return c == Path.DirectorySeparatorChar;
        }

        public static bool IsPartiallyQualified(string path)
        {
            // This is much simpler than Windows where paths can be rooted, but not fully qualified (such as Drive Relative)
            // As long as the path is rooted in Unix it doesn't use the current directory and therefore is fully qualified.
            return string.IsNullOrEmpty(path) || path[0] != Path.DirectorySeparatorChar;
        }
    }
}
