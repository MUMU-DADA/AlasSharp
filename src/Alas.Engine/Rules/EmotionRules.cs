namespace Alas.Engine.Rules;

public enum EmotionControl { KeepExpBonus, PreventGreenFace, PreventYellowFace, PreventRedFace }
public enum EmotionRecovery { NotInDormitory, DormitoryFloor1, DormitoryFloor2 }
public sealed record FleetEmotionSettings(EmotionControl Control, EmotionRecovery Recovery, bool Oath)
{
    public int Speed => (Recovery switch
    {
        EmotionRecovery.NotInDormitory => 20, EmotionRecovery.DormitoryFloor1 => 40,
        EmotionRecovery.DormitoryFloor2 => 50, _ => throw new ArgumentOutOfRangeException(nameof(Recovery))
    } + (Oath ? 10 : 0)) / 10;
    public int Maximum => Recovery == EmotionRecovery.NotInDormitory ? 119 : 150;
    public int Limit => Control switch
    {
        EmotionControl.KeepExpBonus => 120, EmotionControl.PreventGreenFace => 40,
        EmotionControl.PreventYellowFace => 30, EmotionControl.PreventRedFace => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(Control))
    };
    public void Validate()
    {
        if (!Enum.IsDefined(Control) || !Enum.IsDefined(Recovery)) throw new ArgumentException("Unknown emotion settings");
        if (Control == EmotionControl.KeepExpBonus && Recovery == EmotionRecovery.NotInDormitory)
            throw new ArgumentException("Happy experience bonus requires dormitory recovery");
    }
}

/// <summary>Native FleetEmotion/Emotion arithmetic. Records are estimates, never OCR readings.</summary>
public static class EmotionRules
{
    public static readonly SourceFile Source = new("module/combat/emotion.py",
        "78564065ab11d0e44617d1b697fcaf062a9a19e47e314d5f93365c739557b7b6");
    public static bool Calculates(this CampaignEmotionMode mode) => mode is CampaignEmotionMode.Calculate or CampaignEmotionMode.CalculateIgnore;
    public static bool Ignores(this CampaignEmotionMode mode) => mode is CampaignEmotionMode.Ignore or CampaignEmotionMode.CalculateIgnore;
    public static string Name(this CampaignEmotionMode mode) => mode switch
    {
        CampaignEmotionMode.Calculate => "calculate", CampaignEmotionMode.Ignore => "ignore",
        CampaignEmotionMode.CalculateIgnore => "calculate_ignore", CampaignEmotionMode.Nothing => "nothing",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    public static CampaignEmotionMode ParseMode(string value) => value switch
    {
        "calculate" => CampaignEmotionMode.Calculate, "ignore" => CampaignEmotionMode.Ignore,
        "calculate_ignore" => CampaignEmotionMode.CalculateIgnore, "nothing" => CampaignEmotionMode.Nothing,
        _ => throw new ArgumentException("Unknown emotion mode")
    };
    public static long FloorDivide(long value, long divisor)
    {
        if (divisor <= 0) throw new ArgumentOutOfRangeException(nameof(divisor));
        long quotient = Math.DivRem(value, divisor, out long remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
    private static long Tick(DateTimeOffset value) => FloorDivide(
        (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerSecond, 360);
    public static int Recover(int value, DateTimeOffset recorded, DateTimeOffset now, FleetEmotionSettings settings)
    {
        long ticks = Math.Max(Tick(now) - Tick(recorded), 0);
        return (int)Math.Min(Math.Max((long)value, 0) + settings.Speed * ticks, settings.Maximum);
    }
    public static DateTimeOffset RecoveredAt(int current, int expectedReduction, DateTimeOffset now, FleetEmotionSettings settings)
    {
        settings.Validate();
        if (settings.Control == EmotionControl.KeepExpBonus) expectedReduction = Math.Min(expectedReduction, 29);
        long ticks = FloorDivide((long)settings.Limit + expectedReduction - current, settings.Speed);
        return DateTimeOffset.FromUnixTimeSeconds(checked((Tick(now) + ticks + 1) * 360));
    }
    public static (int First, int Second) ExpectedReduction(int battles, FleetOrder order,
        bool mapDoubleBook = false, bool requestedDoubleBook = false)
    {
        if (battles < 0) throw new ArgumentOutOfRangeException(nameof(battles));
        int cost = mapDoubleBook || requestedDoubleBook ? 4 : 2;
        return order switch
        {
            FleetOrder.Fleet1MobFleet2Boss => (checked((battles - 1) * cost), cost),
            FleetOrder.Fleet1BossFleet2Mob => (cost, checked((battles - 1) * cost)),
            FleetOrder.Fleet1AllFleet2Standby => (checked(battles * cost), 0),
            FleetOrder.Fleet1StandbyFleet2All => (0, checked(battles * cost)),
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };
    }
}
