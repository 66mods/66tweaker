using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace Tweaker.Infrastructure.Windows.Legacy;

internal static class CommandTokenizer
{
    internal static List<string> Tokenize(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < command.Length; index++)
        {
            var value = command[index];
            if (value == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(value))
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            if (value == '^' && index + 1 < command.Length) value = command[++index];
            current.Append(value);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    internal static bool IsShellControl(string value) =>
        value is "|" or "||" or "&" or "&&" || value.StartsWith('>') || value.StartsWith("1>") ||
        value.StartsWith("2>") || value.Equals("2>&1", StringComparison.Ordinal);
}

internal sealed record LegacyRegistryCommand(LegacyRegistryTarget Target)
{
    internal static bool TryParse(string text, out LegacyRegistryCommand command)
    {
        command = null!;
        var tokens = CommandTokenizer.Tokenize(text.TrimStart('@', ' ', '\t'));
        if (tokens.Count < 3 || tokens[0].Equals("for", StringComparison.OrdinalIgnoreCase) ||
            !(tokens[0].Equals("reg", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals("reg.exe", StringComparison.OrdinalIgnoreCase)))
            return false;
        var action = tokens[1].ToLowerInvariant();
        if (action is not ("add" or "delete") || !TrySplitHive(tokens[2], out var hive, out var subKey))
            return false;
        string? name = null; string? type = null; string? data = null;
        var defaultValue = false;
        for (var index = 3; index < tokens.Count; index++)
        {
            if (CommandTokenizer.IsShellControl(tokens[index])) break;
            if (tokens[index].Equals("/ve", StringComparison.OrdinalIgnoreCase)) { defaultValue = true; name = string.Empty; continue; }
            if (tokens[index].Equals("/v", StringComparison.OrdinalIgnoreCase) && index + 1 < tokens.Count) { name = tokens[++index]; continue; }
            if (tokens[index].Equals("/t", StringComparison.OrdinalIgnoreCase) && index + 1 < tokens.Count) { type = tokens[++index]; continue; }
            if (tokens[index].Equals("/d", StringComparison.OrdinalIgnoreCase) && index + 1 < tokens.Count) { data = tokens[++index]; continue; }
        }
        if (action == "delete")
        {
            command = new LegacyRegistryCommand(new LegacyRegistryTarget(hive, subKey, name,
                name is null ? LegacyRegistryAction.DeleteKey : LegacyRegistryAction.DeleteValue, null, null));
            return true;
        }
        if (name is null && !defaultValue)
        {
            command = new LegacyRegistryCommand(new LegacyRegistryTarget(
                hive, subKey, null, LegacyRegistryAction.CreateKey, null, null));
            return true;
        }
        if (type is null || data is null || !TryValue(type, data, out var kind, out var value)) return false;
        command = new LegacyRegistryCommand(new LegacyRegistryTarget(
            hive, subKey, name, LegacyRegistryAction.Write, kind, value));
        return true;
    }

    private static bool TrySplitHive(string path, out LegacyRegistryHive hive, out string subKey)
    {
        hive = default; subKey = string.Empty;
        var slash = path.IndexOf('\\');
        if (slash <= 0) return false;
        var root = path[..slash];
        subKey = Environment.ExpandEnvironmentVariables(path[(slash + 1)..]);
        hive = root.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => LegacyRegistryHive.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => LegacyRegistryHive.LocalMachine,
            "HKU" or "HKEY_USERS" => LegacyRegistryHive.Users,
            "HKCR" or "HKEY_CLASSES_ROOT" => LegacyRegistryHive.ClassesRoot,
            _ => (LegacyRegistryHive)(-1)
        };
        return Enum.IsDefined(hive) && subKey.Length is > 0 and <= 2048 && subKey.IndexOf('\0') < 0;
    }

    private static bool TryValue(string type, string data, out RegistryValueKind kind, out object value)
    {
        kind = default; value = null!;
        data = Environment.ExpandEnvironmentVariables(data);
        switch (type.ToUpperInvariant())
        {
            case "REG_DWORD":
                kind = RegistryValueKind.DWord;
                var style = data.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer;
                var number = data.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? data[2..] : data;
                if (!uint.TryParse(number, style, CultureInfo.InvariantCulture, out var dword)) return false;
                value = unchecked((int)dword); return true;
            case "REG_SZ":
                kind = RegistryValueKind.String; value = data; return true;
            case "REG_BINARY":
                var compact = new string(data.Where(Uri.IsHexDigit).ToArray());
                if (compact.Length == 0 || compact.Length % 2 != 0) return false;
                kind = RegistryValueKind.Binary; value = Convert.FromHexString(compact); return true;
            default: return false;
        }
    }
}

internal static class LegacyCleanup
{
    internal static bool TryExecute(string command)
    {
        var tokens = CommandTokenizer.Tokenize(command);
        if (tokens.Count < 2) return false;
        var recursive = tokens.Any(x => x.Equals("/s", StringComparison.OrdinalIgnoreCase));
        var candidates = tokens.Skip(1).Where(x => !x.StartsWith('/') && !CommandTokenizer.IsShellControl(x)).ToArray();
        var performed = false;
        foreach (var candidate in candidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (!TryGetAllowedRoot(expanded, out var allowedRoot)) continue;
            var parent = Path.GetDirectoryName(expanded);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) continue;
            var pattern = Path.GetFileName(expanded);
            var canonicalParent = Path.GetFullPath(parent);
            if (!IsContained(allowedRoot, canonicalParent) || IsReparsePoint(canonicalParent)) continue;
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(canonicalParent, pattern, option))
            {
                if (!IsContained(allowedRoot, Path.GetFullPath(file)) || HasReparseAncestor(allowedRoot, file)) continue;
                File.Delete(file); performed = true;
            }
        }
        return performed;
    }

    private static bool TryGetAllowedRoot(string path, out string root)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var temp = Path.GetTempPath();
        var allowed = new[]
        {
            temp, Path.Combine(windows, "Temp"), Path.Combine(windows, "Prefetch"),
            Path.Combine(local, "Temp"), Path.Combine(local, "D3DSCache")
        }.Select(Path.GetFullPath).OrderByDescending(x => x.Length);
        var full = Path.GetFullPath(path);
        root = allowed.FirstOrDefault(x => IsContained(x, full)) ?? string.Empty;
        return root.Length > 0;
    }

    private static bool IsContained(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool HasReparseAncestor(string root, string path)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (current is not null && IsContained(root, current.FullName))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            current = current.Parent;
        }
        return false;
    }
}
