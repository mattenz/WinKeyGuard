using System.Text.Json;
using HidSharp;

namespace WinKeyGuard;

enum ApplyResult { Ok, NotConnected, Mismatch, NoBackup, Error }

sealed class KeymapBackup
{
    public required int Layers { get; init; }
    public required int Rows { get; init; }
    public required int Cols { get; init; }
    public required int ProductId { get; init; }
    public required byte[] Keymap { get; init; }
    public required List<int> GuiOffsets { get; init; }   // byte offsets of KC_LGUI / KC_RGUI entries
}

// All members must be called under the app's HID lock (one operation at a time).
sealed class KeyboardController
{
    const int VendorId = 0x3434;   // Keychron
    const byte CmdGetProtocolVersion = 0x01;
    const byte CmdGetLayerCount = 0x11;
    const byte CmdGetBuffer = 0x12;
    const byte CmdSetBuffer = 0x13;

    readonly Config _cfg;

    public KeyboardController(Config cfg) => _cfg = cfg;

    // Makes the keyboard match the desired state. Stateless: it compares the live keymap with
    // the backup every time, so it also repairs a keyboard left disabled by a crash.
    //   disable = true  -> every selected Win key entry (left/right in the config) becomes KC_NO,
    //                      unselected ones are set back to their original keycode
    //   disable = false -> every Win key entry from the backup is set back to its original keycode
    public ApplyResult Apply(bool disable, out string message)
    {
        var backup = LoadBackup(out message);
        if (backup == null)
        {
            if (File.Exists(AppPaths.Backup)) return ApplyResult.NoBackup;   // unreadable; message already set

            // First run: take the backup from the keyboard as it is right now.
            var created = CreateBackup(overwrite: false, out message);
            if (created != ApplyResult.Ok)
                return created == ApplyResult.NotConnected ? ApplyResult.NotConnected : ApplyResult.NoBackup;

            backup = LoadBackup(out message);
            if (backup == null) return ApplyResult.NoBackup;
        }

        using var stream = Open(backup.ProductId);
        if (stream == null)
        {
            message = "Q6 Max not reachable over USB.";
            return ApplyResult.NotConnected;
        }

        try
        {
            int liveLayers = Exchange(stream, CmdGetLayerCount)[2];
            if (liveLayers != backup.Layers)
            {
                message = $"Layer count changed ({liveLayers} vs {backup.Layers} in backup). Refresh the keymap backup.";
                return ApplyResult.Mismatch;
            }

            var live = ReadKeymap(stream, backup.Keymap.Length);
            var expected = (byte[])live.Clone();
            var writes = new List<(int off, ushort kc)>();

            foreach (int off in backup.GuiOffsets)
            {
                ushort orig = Key(backup.Keymap, off);
                ushort cur = Key(live, off);
                ushort target = disable && IsSelected(orig) ? (ushort)0x0000 : orig;

                if (cur == target) continue;

                // Only ever touch an entry that holds the original key or KC_NO. Anything else means
                // the keymap was edited since the backup was taken, so do not guess.
                if (cur != orig && cur != 0x0000)
                {
                    message = $"Keymap changed at offset {off} (found 0x{cur:X4}). Refresh the keymap backup.";
                    return ApplyResult.Mismatch;
                }

                writes.Add((off, target));
                expected[off] = (byte)(target >> 8);
                expected[off + 1] = (byte)(target & 0xFF);
            }

            if (writes.Count == 0)
            {
                message = disable ? "Win keys already disabled." : "Win keys already enabled.";
                return ApplyResult.Ok;
            }

            foreach (var (off, kc) in writes) WriteKey(stream, off, kc);

            var after = ReadKeymap(stream, backup.Keymap.Length);
            if (!after.AsSpan().SequenceEqual(expected))
            {
                message = "Verify failed after writing the keymap.";
                return ApplyResult.Error;
            }

            message = $"{(disable ? "Disabled" : "Restored")} {writes.Count} Win key entr{(writes.Count == 1 ? "y" : "ies")}.";
            return ApplyResult.Ok;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return ApplyResult.Error;
        }
    }

    // Saves the live keymap as the backup. Refuses if it looks like the Win keys are currently disabled.
    public ApplyResult CreateBackup(bool overwrite, out string message)
    {
        if (!overwrite && File.Exists(AppPaths.Backup))
        {
            message = "Backup already exists.";
            return ApplyResult.Ok;
        }

        using var stream = Open(_cfg.ProductId);
        if (stream == null)
        {
            message = "Keyboard not reachable over USB, so no backup could be made yet.";
            return ApplyResult.NotConnected;
        }

        try
        {
            int layers = Exchange(stream, CmdGetLayerCount)[2];
            var keymap = ReadKeymap(stream, layers * _cfg.Rows * _cfg.Cols * 2);

            var old = LoadBackup(out _);
            if (old != null && old.GuiOffsets.Any(o => o + 1 < keymap.Length && Key(keymap, o) == 0x0000))
            {
                message = "The Win keys look disabled right now. Restore them first, then refresh the backup.";
                return ApplyResult.Mismatch;
            }

            bool anyGui = false;
            for (int off = 0; off < keymap.Length; off += 2)
            {
                ushort kc = Key(keymap, off);
                if (kc == 0x00E3 || kc == 0x00E7) { anyGui = true; break; }
            }

            if (!anyGui)
            {
                message = "No Win key entries found in the keymap (already disabled?). Backup not saved.";
                return ApplyResult.Mismatch;
            }

            var json = new
            {
                savedUtc = DateTime.UtcNow,
                vendorId = VendorId,
                productId = _cfg.ProductId,
                layers,
                rows = _cfg.Rows,
                cols = _cfg.Cols,
                keymapHex = Convert.ToHexString(keymap)
            };
            File.WriteAllText(AppPaths.Backup,
                JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));

            message = "Keymap backup saved.";
            return ApplyResult.Ok;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return ApplyResult.Error;
        }
    }

    // Reads a backup file (also compatible with the one written by KeychronProbe).
    static KeymapBackup? LoadBackup(out string error)
    {
        error = "";
        if (!File.Exists(AppPaths.Backup))
        {
            error = "No keymap backup yet.";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.Backup));
            var r = doc.RootElement;
            int layers = r.GetProperty("layers").GetInt32();
            int rows = r.GetProperty("rows").GetInt32();
            int cols = r.GetProperty("cols").GetInt32();
            int pid = r.GetProperty("productId").GetInt32();
            var keymap = Convert.FromHexString(r.GetProperty("keymapHex").GetString()!);

            if (keymap.Length != layers * rows * cols * 2)
            {
                error = "Keymap backup is inconsistent (size does not match layers x rows x cols).";
                return null;
            }

            var gui = new List<int>();
            for (int off = 0; off < keymap.Length; off += 2)
            {
                ushort kc = Key(keymap, off);
                if (kc == 0x00E3 || kc == 0x00E7) gui.Add(off);
            }

            if (gui.Count == 0)
            {
                error = "Keymap backup contains no Win key entries. Replace it with a backup taken while the Win keys work.";
                return null;
            }

            return new KeymapBackup
            {
                Layers = layers, Rows = rows, Cols = cols, ProductId = pid, Keymap = keymap, GuiOffsets = gui
            };
        }
        catch (Exception ex)
        {
            error = "Keymap backup unreadable: " + ex.Message;
            return null;
        }
    }

    bool IsSelected(ushort keycode) => keycode == 0x00E3 ? _cfg.DisableLeftWin : _cfg.DisableRightWin;

    // ---- HID / VIA plumbing ------------------------------------------------------------

    static HidStream? Open(int productId)
    {
        foreach (var d in DeviceList.Local.GetHidDevices(VendorId, productId))
        {
            try
            {
                // The VIA raw HID interface uses 32-byte reports (33 with the report ID byte).
                if (d.GetMaxInputReportLength() != 33 || d.GetMaxOutputReportLength() != 33) continue;
                if (!d.TryOpen(out HidStream s)) continue;

                s.ReadTimeout = 1500;
                s.WriteTimeout = 1500;
                try
                {
                    Exchange(s, CmdGetProtocolVersion);
                    return s;
                }
                catch
                {
                    s.Dispose();
                }
            }
            catch { }
        }
        return null;
    }

    static byte[] Exchange(HidStream s, params byte[] payload)
    {
        var req = new byte[33];                       // [0] = report ID 0
        Array.Copy(payload, 0, req, 1, payload.Length);
        s.Write(req);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var resp = new byte[33];
            s.Read(resp);
            if (resp[1] == payload[0]) return resp;   // skip unrelated reports
        }
        throw new IOException("No matching response from keyboard.");
    }

    static byte[] ReadKeymap(HidStream s, int total)
    {
        var buf = new byte[total];
        for (int off = 0; off < total; off += 28)
        {
            int size = Math.Min(28, total - off);
            var r = Exchange(s, CmdGetBuffer, (byte)(off >> 8), (byte)(off & 0xFF), (byte)size);
            Array.Copy(r, 5, buf, off, size);
        }
        return buf;
    }

    static void WriteKey(HidStream s, int offset, ushort keycode)
    {
        Exchange(s, CmdSetBuffer,
            (byte)(offset >> 8), (byte)(offset & 0xFF), 2,
            (byte)(keycode >> 8), (byte)(keycode & 0xFF));
    }

    static ushort Key(byte[] buf, int off) => (ushort)((buf[off] << 8) | buf[off + 1]);
}
