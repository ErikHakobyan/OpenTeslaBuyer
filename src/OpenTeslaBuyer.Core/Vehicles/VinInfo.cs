namespace OpenTeslaBuyer.Core.Vehicles;

/// <summary>Decodes the parts of a Tesla VIN that follow the public VIN standard (maker, model, model year, plant).</summary>
public sealed record VinInfo(string Vin)
{
    private const string YearCodes = "ABCDEFGHJKLMNPRSTVWXY"; // 2010–2030; I, O, Q, U, Z are never used
    private static readonly int[] Weights = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];

    public bool IsWellFormed => Vin.Length == 17 && Vin.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)) && !Vin.Any(c => c is 'I' or 'O' or 'Q');

    public string Maker => Vin.Length < 3 ? "Unknown" : Vin[..3] switch
    {
        "5YJ" or "7SA" => "Tesla, USA",
        "7G2" => "Tesla (truck), USA",
        "LRW" => "Tesla, China",
        "XP7" => "Tesla, Germany",
        "SFZ" => "Tesla Roadster, UK-built",
        var wmi => $"Unknown ({wmi})",
    };

    public TeslaModel Model => Vin.Length < 4 ? TeslaModel.Unknown : Vin[3] switch
    {
        'S' => TeslaModel.ModelS,
        'X' => TeslaModel.ModelX,
        '3' => TeslaModel.Model3,
        'Y' => TeslaModel.ModelY,
        'C' => TeslaModel.Cybertruck,
        _ => TeslaModel.Unknown,
    };

    public int? ModelYear
    {
        get
        {
            if (Vin.Length < 10)
                return null;
            var code = Vin[9];
            var index = YearCodes.IndexOf(code);
            if (index >= 0)
                return 2010 + index;
            return char.IsAsciiDigit(code) && code != '0' ? 2000 + (code - '0') : null;
        }
    }

    public string Plant => Vin.Length < 11 ? "Unknown" : Vin[10] switch
    {
        'F' => "Fremont, California",
        'A' => "Austin, Texas",
        'B' => "Berlin, Germany",
        'C' => "Shanghai, China",
        var code => $"Unknown ({code})",
    };

    /// <summary>
    /// Whether position 9 matches the North American check digit. Null for VINs where the check digit is not mandatory
    /// (European-built cars). A mismatch on a US car usually means the VIN pages were read incorrectly.
    /// </summary>
    public bool? CheckDigitValid
    {
        get
        {
            if (!IsWellFormed || Vin[0] is not ('1' or '4' or '5' or '7'))
                return null;
            return ComputeCheckDigit(Vin) == Vin[8];
        }
    }

    public static char ComputeCheckDigit(string vin)
    {
        var sum = 0;
        for (var i = 0; i < 17; i++)
            sum += Transliterate(vin[i]) * Weights[i];
        var remainder = sum % 11;
        return remainder == 10 ? 'X' : (char)('0' + remainder);
    }

    private static int Transliterate(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        'A' or 'J' => 1,
        'B' or 'K' or 'S' => 2,
        'C' or 'L' or 'T' => 3,
        'D' or 'M' or 'U' => 4,
        'E' or 'N' or 'V' => 5,
        'F' or 'W' => 6,
        'G' or 'P' or 'X' => 7,
        'H' or 'Y' => 8,
        'R' or 'Z' => 9,
        _ => 0,
    };
}

public enum TeslaModel
{
    Unknown,
    ModelS,
    ModelX,
    Model3,
    ModelY,
    Cybertruck,
}
