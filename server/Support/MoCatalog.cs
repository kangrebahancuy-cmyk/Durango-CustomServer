using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Durango.Online;

/// <summary>
/// GNU gettext (.mo) reader used to translate the game's original Korean strings through a locale catalog.
///
/// Why the server needs its own translations even though the game has a catalog:
/// Item and building names shown to players do not pass through the client's catalog;
/// they are sent by the server as already-resolved text:
///   • <c>client/Durango.Logic.Item/ItemData.cs:132</c> — <c>Name = itemInfo.Name;</c>
/// (the client displays Messages.Item.Name directly and does not call T._()).
/// The client translates only data it loads from its own files through
///     <c>client/.../GettextConverter.cs</c> → <c>T.ParseMsgIdAndGetString(msgid, dict)</c>
/// Therefore, adding a locales/th directory alone cannot translate names sent directly by the server.
///
/// Why the JSON data cannot be translated automatically:
/// data/assets/** stores strings as {"msgid": {locale: translation}}.
/// In the extracted data, most translation values are null, so
/// Durango.Utils.Gettext falls back to the original msgid.
/// The actual translation table is stored separately in messages.mo (33,262 entries).
///
/// GNU gettext file format:
/// magic 0x950412de (byte-reversed for big-endian),
/// offset tables for msgid and msgstr; strings are UTF-8 without a trailing null in their recorded lengths.
/// The first entry (empty msgid) is the file header, not a translation, so it is skipped.
///
/// ⚠️ msgid values containing <c>\u0004</c> include gettext context (msgctxt); keep both forms:
/// the full key and the suffix after <c>\u0004</c> for context-free server messages.
/// </summary>
public static class MoCatalog
{
    private const uint MagicLittleEndian = 0x950412deu;
    private const uint MagicBigEndian = 0xde120495u;

    private static Dictionary<string, string> _map;

/// <summary>Number of translations loaded; 0 means not loaded or no file was found.</summary>
    public static int Count => _map?.Count ?? 0;

/// <summary>Whether a non-empty catalog has been loaded.</summary>
    public static bool Ready => _map is { Count: > 0 };

    /// <summary>
/// Load translations from the data directory once during startup.
    ///
/// Looks for locales/&lt;language&gt;/LC_MESSAGES/messages.mo.
/// A missing catalog is non-fatal; it logs a warning once and leaves strings unchanged.
    /// </summary>
    public static void Load(string dataDir, string language = "th")
    {
        if (_map != null) return;
        _map = new Dictionary<string, string>(StringComparer.Ordinal);

        string path = Path.Combine(dataDir ?? "", "locales", language, "LC_MESSAGES", "messages.mo");
        if (!File.Exists(path))
        {
            Console.WriteLine($"[locale] Translation catalog not found at {path}; original game strings will be used.");
            return;
        }

        try
        {
            Parse(File.ReadAllBytes(path), _map);
            Console.WriteLine($"[locale] Loaded { _map.Count } translations for language {language}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[locale] Failed to read {path}: {e.Message}; original game strings will be used.");
        }
    }

    /// <summary>
/// Translate a string; return the original value when no translation exists.
    /// </summary>
    public static string Translate(string text)
    {
        if (string.IsNullOrEmpty(text) || _map == null || _map.Count == 0) return text;
        if (_map.TryGetValue(text, out string translated) && !string.IsNullOrEmpty(translated))
        {
            return translated;
        }
        return text;
    }

    private static void Parse(byte[] data, Dictionary<string, string> into)
    {
        if (data.Length < 20) throw new InvalidDataException("File is too short to be a .mo catalog");

        uint magic = BitConverter.ToUInt32(data, 0);
        bool swap;
        if (magic == MagicLittleEndian) swap = !BitConverter.IsLittleEndian;
        else if (magic == MagicBigEndian) swap = BitConverter.IsLittleEndian;
        else throw new InvalidDataException($"Invalid gettext magic value ({magic:x8})");

        int count = (int)ReadUInt(data, 8, swap);
        int originalTable = (int)ReadUInt(data, 12, swap);
        int translationTable = (int)ReadUInt(data, 16, swap);

        for (int i = 0; i < count; i++)
        {
            int oLen = (int)ReadUInt(data, originalTable + i * 8, swap);
            int oOff = (int)ReadUInt(data, originalTable + i * 8 + 4, swap);
            int tLen = (int)ReadUInt(data, translationTable + i * 8, swap);
            int tOff = (int)ReadUInt(data, translationTable + i * 8 + 4, swap);

            if (oLen <= 0) continue;                                   // First entry is the file header.
            if (oOff < 0 || tOff < 0 || oOff + oLen > data.Length || tOff + tLen > data.Length) continue;

            string msgid = Encoding.UTF8.GetString(data, oOff, oLen);
            string msgstr = Encoding.UTF8.GetString(data, tOff, tLen);
            if (string.IsNullOrEmpty(msgstr)) continue;

            // Plural forms are separated by \\0; use the first form only.
            int nul = msgstr.IndexOf('\0');
            if (nul >= 0) msgstr = msgstr.Substring(0, nul);

            into[msgid] = msgstr;

            // Context is separated by \u0004; also keep a context-free key for server messages.
            int ctx = msgid.IndexOf('');
            if (ctx >= 0 && ctx + 1 < msgid.Length)
            {
                string bare = msgid.Substring(ctx + 1);
                if (!into.ContainsKey(bare)) into[bare] = msgstr;
            }
        }
    }

    private static uint ReadUInt(byte[] data, int offset, bool swap)
    {
        if (offset < 0 || offset + 4 > data.Length) throw new InvalidDataException("Read offset is outside the file");
        uint value = BitConverter.ToUInt32(data, offset);
        return swap ? BinaryPrimitivesReverse(value) : value;
    }

    private static uint BinaryPrimitivesReverse(uint value) =>
        (value >> 24) | ((value >> 8) & 0x0000FF00u) | ((value << 8) & 0x00FF0000u) | (value << 24);
}
