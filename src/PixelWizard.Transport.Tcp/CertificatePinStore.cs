using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixelWizard.Transport.Tcp
{
    /// <summary>
    /// Persists trust-on-first-use certificate pins (host:port -> SHA-256 fingerprint hex)
    /// to a JSON file. A missing file is treated as an empty pin set (fresh install).
    /// A file that exists but cannot be read as valid data is treated as corruption and
    /// throws <see cref="CertificatePinStoreCorruptedException"/> — it never falls back to
    /// an empty pin set, since that would let a corrupted-on-disk store silently reopen
    /// trust for a host that was already pinned.
    /// </summary>
    public sealed class CertificatePinStore
    {
        private readonly string _path;

        // Keyed by normalized file path rather than instance, so two CertificatePinStore
        // instances pointed at the same file (as tests do) still serialize against each
        // other, and stores at different paths never contend.
        private static readonly ConcurrentDictionary<string, object> PathLocks = new();

        public CertificatePinStore(string? path = null)
        {
            _path = path ?? DefaultPath;
        }

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PixelWizardConnect", "known_hosts.json");

        private object Lock => PathLocks.GetOrAdd(Path.GetFullPath(_path), _ => new object());

        public string? TryGetPin(string key)
        {
            lock (Lock)
            {
                return Load().TryGetValue(key, out var fingerprint) ? fingerprint : null;
            }
        }

        public void Pin(string key, string fingerprint)
        {
            lock (Lock)
            {
                var pins = Load();
                pins[key] = fingerprint;
                Save(pins);
            }
        }

        public bool Forget(string key)
        {
            lock (Lock)
            {
                var pins = Load();
                if (!pins.Remove(key)) return false;
                Save(pins);
                return true;
            }
        }

        private Dictionary<string, string> Load()
        {
            if (!File.Exists(_path)) return new Dictionary<string, string>();

            string json;
            try
            {
                json = File.ReadAllText(_path);
            }
            catch (IOException ex)
            {
                throw new CertificatePinStoreCorruptedException($"Could not read pin store at '{_path}'.", ex);
            }

            if (string.IsNullOrWhiteSpace(json))
                throw new CertificatePinStoreCorruptedException($"Pin store at '{_path}' exists but is empty.");

            PinFile? data;
            try
            {
                data = JsonSerializer.Deserialize<PinFile>(json);
            }
            catch (JsonException ex)
            {
                throw new CertificatePinStoreCorruptedException($"Pin store at '{_path}' is not valid JSON.", ex);
            }

            if (data?.Pins == null)
                throw new CertificatePinStoreCorruptedException($"Pin store at '{_path}' does not match the expected schema.");

            foreach (var (hostKey, fingerprint) in data.Pins)
            {
                if (!IsValidFingerprint(fingerprint))
                    throw new CertificatePinStoreCorruptedException(
                        $"Pin store at '{_path}' has a malformed fingerprint for '{hostKey}'.");
            }

            return new Dictionary<string, string>(data.Pins);
        }

        private void Save(Dictionary<string, string> pins)
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new PinFile { Version = 1, Pins = pins },
                new JsonSerializerOptions { WriteIndented = true });

            // Write to a temp file then atomically replace, so a crash mid-write never
            // leaves a truncated file at _path — a truncated/partial write must never be
            // mistaken for "no pins recorded".
            var tmpPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _path, overwrite: true);
        }

        private static bool IsValidFingerprint(string fingerprint) =>
            fingerprint.Length == 64 && fingerprint.All(Uri.IsHexDigit);

        private sealed class PinFile
        {
            [JsonPropertyName("version")]
            public int Version { get; set; }

            [JsonPropertyName("pins")]
            public Dictionary<string, string>? Pins { get; set; }
        }
    }
}
