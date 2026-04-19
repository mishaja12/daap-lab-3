namespace WordGame.Server;

public class WordValidator
{
    private readonly HashSet<string> _dictionary = new(StringComparer.OrdinalIgnoreCase);
    public string DictionaryHash { get; private set; } = "";

    public WordValidator()
    {
        LoadDictionary();
    }

    private void LoadDictionary()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "words.txt");
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                var word = line.Trim().ToLowerInvariant();
                if (word.Length >= 2)
                    _dictionary.Add(word);
            }
        }
        else
        {
            // Fallback: minimal embedded word list for demo
            var fallback = new[] { "star", "rat", "tar", "art", "sat", "at", "as", "tea", "eat", "ate",
                "ear", "are", "era", "sea", "see", "set", "met", "let", "bet", "get", "net", "pet", "wet",
                "cat", "act", "bat", "hat", "mat", "pat", "fat", "can", "man", "pan", "tan", "ran", "van",
                "dog", "god", "log", "fog", "got", "lot", "not", "pot", "hot", "dot", "rot", "tot",
                "red", "bed", "fed", "led", "wed", "den", "pen", "ten", "hen", "men",
                "big", "dig", "fig", "pig", "rig", "wig", "bit", "fit", "hit", "kit", "lit", "pit", "sit", "wit",
                "one", "two", "the", "and", "for", "are", "but", "not", "you", "all", "can", "had", "her", "his", "was" };
            foreach (var w in fallback)
                _dictionary.Add(w);
        }

        using var sha = System.Security.Cryptography.SHA256.Create();
        var content = string.Join('\n', _dictionary.OrderBy(x => x, StringComparer.Ordinal));
        DictionaryHash = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content)));
    }

    public (bool Valid, string? Error) Validate(string word, string offeredLetters)
    {
        if (string.IsNullOrWhiteSpace(word))
            return (false, "Enter a word.");

        var normalized = word.Trim().ToLowerInvariant();
        if (normalized.Length < 2)
            return (false, "Word must be at least 2 letters.");

        if (!_dictionary.Contains(normalized))
            return (false, $"'{normalized}' is not in the dictionary.");

        var offered = offeredLetters.ToLowerInvariant().Where(char.IsLetter).ToList();
        var used = new List<char>(offered);

        foreach (var c in normalized)
        {
            var idx = used.IndexOf(c);
            if (idx < 0)
                return (false, $"'{normalized}' uses letters not in the set. Use only: {string.Join(", ", offeredLetters.ToUpperInvariant().Where(char.IsLetter).Distinct())}");
            used.RemoveAt(idx);
        }

        return (true, null);
    }

    public bool IsValid(string word, string offeredLetters) => Validate(word, offeredLetters).Valid;
}
