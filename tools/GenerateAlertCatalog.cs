#:property PublishAot=false
// Regenerates src/OpenTeslaBuyer.Core/Alerts/alert-catalog-model3.json from a Model 3 DBC file.
//
//   dotnet run tools/GenerateAlertCatalog.cs -- path/to/tesla_model3.dbc src/OpenTeslaBuyer.Core/Alerts/alert-catalog-model3.json
//
// Source used: https://github.com/onyx-m2/onyx-m2-dbc (tesla_model3.dbc). Every "...alertMatrix"/"...warningMatrix"
// message on the vehicle bus (VEH) is taken, with each one-bit alert signal's exact page and bit position.
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run tools/GenerateAlertCatalog.cs -- <input.dbc> <output.json>");
    return 1;
}

var message = new Regex(@"^BO_ (\d+) (\w+): \d+ (\w+)");
var signal = new Regex(@"^\s*SG_ (\w+)\s*(M|m\d+)?\s*:\s*(\d+)\|(\d+)@1\+");
var alertName = new Regex(@"^([A-Z0-9]+)_([aw]\d{3})_(\w+)$");
var skipped = new Regex("unused|do_?not_?use|deprecated|placeholder", RegexOptions.IgnoreCase);

var messages = new List<Dictionary<string, object?>>();
List<Dictionary<string, object?>>? alerts = null;
Dictionary<string, object?>? current = null;

foreach (var line in File.ReadLines(args[0]))
{
    if (message.Match(line) is { Success: true } m)
    {
        current = null;
        if (Regex.IsMatch(m.Groups[2].Value, "(alert|warning)Matrix") && m.Groups[3].Value == "VEH")
        {
            alerts = [];
            current = new() { ["id"] = int.Parse(m.Groups[1].Value), ["name"] = m.Groups[2].Value, ["pageStart"] = null, ["pageLength"] = null, ["alerts"] = alerts };
            messages.Add(current);
        }

        continue;
    }

    if (current is null || signal.Match(line) is not { Success: true } s)
        continue;

    if (s.Groups[2].Value == "M")
    {
        current["pageStart"] = int.Parse(s.Groups[3].Value);
        current["pageLength"] = int.Parse(s.Groups[4].Value);
        continue;
    }

    if (s.Groups[4].Value != "1" || alertName.Match(s.Groups[1].Value) is not { Success: true } a || skipped.IsMatch(a.Groups[3].Value))
        continue;

    alerts!.Add(new()
    {
        ["code"] = $"{a.Groups[1].Value}_{a.Groups[2].Value}",
        ["name"] = a.Groups[3].Value,
        ["page"] = s.Groups[2].Value.StartsWith('m') ? int.Parse(s.Groups[2].Value[1..]) : null,
        ["bit"] = int.Parse(s.Groups[3].Value),
    });
}

messages.RemoveAll(m => ((List<Dictionary<string, object?>>)m["alerts"]!).Count == 0);
var catalog = new Dictionary<string, object>
{
    ["source"] = "onyx-m2/onyx-m2-dbc tesla_model3.dbc (community reverse engineering; alert names are Tesla's)",
    ["messages"] = messages,
};
File.WriteAllText(args[1], JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{messages.Count} messages, {messages.Sum(m => ((List<Dictionary<string, object?>>)m["alerts"]!).Count)} alerts -> {args[1]}");
return 0;
