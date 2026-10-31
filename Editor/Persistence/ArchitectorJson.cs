using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Architector.Editor
{
    public static class ArchitectorJson
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new(false);

        public static T Read<T>(string path)
        {
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var value = JsonUtility.FromJson<T>(json);
                if (value == null)
                    throw new ArchitectorDataException($"Document '{path}' is empty or invalid.");
                return value;
            }
            catch (ArchitectorDataException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ArchitectorDataException($"Failed to read '{path}': {exception.Message}");
            }
        }

        public static string Serialize<T>(T value)
        {
            return JsonUtility.ToJson(value, true).Replace("\r\n", "\n") + "\n";
        }

        public static T Deserialize<T>(string json, string description = "JSON document")
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArchitectorDataException($"{description} is empty.");

            try
            {
                var value = JsonUtility.FromJson<T>(json);
                if (value == null)
                    throw new ArchitectorDataException($"{description} is invalid.");
                return value;
            }
            catch (ArchitectorDataException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ArchitectorDataException($"Failed to parse {description}: {exception.Message}");
            }
        }

        public static T Clone<T>(T value)
        {
            return JsonUtility.FromJson<T>(Serialize(value));
        }

        public static void WriteAtomic<T>(string path, T value)
        {
            WriteTextAtomic(path, Serialize(value));
        }

        public static void WriteTextAtomic(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
                throw new ArchitectorDataException($"Cannot determine directory for '{path}'.");

            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, content, Utf8WithoutBom);
                if (File.Exists(path))
                    File.Replace(temporaryPath, path, null);
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }
}
