using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyEDA_Loader
{
    public sealed class ModelZInfo
    {
        public double OffsetFromOrigin { get; set; }
        public double Height { get; set; }
    }

    public static class ModelZInfoCache
    {
        private const string CacheVersion = "signed-z-v2";

        public static Task<ModelZInfo> GetOrCreateAsync(
            string modelUuid,
            Func<Task<byte[]>> loadRawObj,
            CancellationToken cancellationToken)
        {
            return GetOrCreateAtPathAsync(GetDefaultCachePath(modelUuid), loadRawObj, cancellationToken);
        }

        public static string GetDefaultCachePath(string modelUuid)
        {
            return Path.Combine(
                GetModelZInfoCacheDirectory(),
                ModelCacheSafeFileName(modelUuid) + ".zinfo");
        }

        internal static ModelZInfo GetOrCreate(string cachePath, Func<byte[]> loadRawObj)
        {
            if (TryRead(cachePath, out ModelZInfo cached))
                return cached;
            if (loadRawObj == null)
                throw new ArgumentNullException(nameof(loadRawObj));

            ModelZInfo parsed = ParseRawObj(loadRawObj());
            Write(cachePath, parsed);
            return parsed;
        }

        internal static async Task<ModelZInfo> GetOrCreateAtPathAsync(
            string cachePath,
            Func<Task<byte[]>> loadRawObj,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryRead(cachePath, out ModelZInfo cached))
                return cached;
            if (loadRawObj == null)
                throw new ArgumentNullException(nameof(loadRawObj));

            byte[] rawObj = await loadRawObj().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            ModelZInfo parsed = ParseRawObj(rawObj);
            Write(cachePath, parsed);
            return parsed;
        }

        internal static ModelZInfo ParseRawObj(byte[] rawObj)
        {
            if (rawObj == null)
                throw new ArgumentNullException(nameof(rawObj));

            double? minZ = null;
            double? maxZ = null;

            using (var reader = new StringReader(Encoding.UTF8.GetString(rawObj)))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("v ", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4 ||
                        !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                    {
                        continue;
                    }

                    if (!minZ.HasValue || z < minZ)
                        minZ = z;
                    if (!maxZ.HasValue || z > maxZ)
                        maxZ = z;
                }
            }

            if (!minZ.HasValue || !maxZ.HasValue)
                throw new InvalidDataException("No vertices found in OBJ file.");

            return new ModelZInfo
            {
                OffsetFromOrigin = -minZ.Value,
                Height = Math.Max(0, maxZ.Value - minZ.Value)
            };
        }

        private static bool TryRead(string cachePath, out ModelZInfo zInfo)
        {
            zInfo = null;
            if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
                return false;

            try
            {
                string[] lines = File.ReadAllLines(cachePath);
                if (lines.Length != 3 || lines[2] != CacheVersion ||
                    !double.TryParse(lines[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double offset) ||
                    !double.TryParse(lines[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double height))
                {
                    return false;
                }

                zInfo = new ModelZInfo
                {
                    OffsetFromOrigin = offset,
                    Height = height
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Write(string cachePath, ModelZInfo zInfo)
        {
            if (string.IsNullOrWhiteSpace(cachePath) || zInfo == null)
                return;

            string directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllLines(
                cachePath,
                new[]
                {
                    zInfo.OffsetFromOrigin.ToString("R", CultureInfo.InvariantCulture),
                    zInfo.Height.ToString("R", CultureInfo.InvariantCulture),
                    CacheVersion
                },
                Encoding.UTF8);
        }

        private static string GetModelZInfoCacheDirectory()
        {
            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root = string.IsNullOrWhiteSpace(localApplicationData)
                ? Path.Combine(Path.GetTempPath(), "EasyEDA-Loader")
                : Path.Combine(localApplicationData, "EasyEDA-Loader");
            return Path.Combine(root, "ModelCache", "ZInfo");
        }

        private static string ModelCacheSafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                value = Guid.NewGuid().ToString("N");

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                value = value.Replace(invalidChar, '_');

            return value;
        }
    }
}
