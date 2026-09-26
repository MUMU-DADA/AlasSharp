using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public enum MapScanMode { Normal, Init, Carrier, Movable, Decoy }
public sealed record CellObservation(bool IsSubmarine = false, bool IsCaughtBySiren = false,
    bool IsFleet = false, bool IsCurrentFleet = false, bool IsBoss = false, bool IsSiren = false,
    bool IsEnemy = false, bool IsMystery = false, bool IsAmmo = false, bool IsMissileAttack = false,
    int EnemyScale = 0, string? EnemyGenre = null);

/// <summary>Direct GridInfo port. All authoritative game state and observation merging stay in C#.</summary>
public sealed class CellState : IEquatable<CellState>
{
    public static readonly SourceFile Source = new("module/map_detection/grid_info.py", "ddc95643dca6bd3f4d0840cf18185bd1a9c7ec57231698184a9d64141e23cde6");
    public Cell Location { get; }
    public bool IsOs { get; set; }
    public bool IsLand { get; set; }
    public bool IsSpawnPoint { get; set; }
    public bool IsSubmarineSpawnPoint { get; set; }
    public bool MayEnemy { get; set; }
    public bool MayBoss { get; set; }
    public bool MayMystery { get; set; }
    public bool MayAmmo { get; set; }
    public bool MaySiren { get; set; }
    public bool MayAmbush { get; set; }
    public bool IsEnemy { get; set; }
    public bool IsBoss { get; set; }
    public bool IsMystery { get; set; }
    public bool IsAmmo { get; set; }
    public bool IsFleet { get; set; }
    public bool IsCurrentFleet { get; set; }
    public bool IsSubmarine { get; set; }
    public bool IsSiren { get; set; }
    public bool IsPortal { get; set; }
    public Cell? PortalLink { get; set; }
    public bool IsMaze { get; set; }
    public IReadOnlyList<int> MazeRound { get; set; } = Array.AsReadOnly(new[] { 0, 1, 2 });
    public IReadOnlyList<CellState>? MazeNearby { get; set; }
    public int EnemyScale { get; set; }
    public string? EnemyGenre { get; set; }
    public bool IsCleared { get; set; }
    public bool IsCaughtBySiren { get; set; }
    public bool IsCarrier { get; set; }
    public bool IsMovable { get; set; }
    public bool IsMechanismTrigger { get; set; }
    public bool IsMechanismBlock { get; set; }
    public IReadOnlyList<CellState>? MechanismTrigger { get; set; }
    public IReadOnlyList<CellState>? MechanismBlock { get; set; }
    public double MechanismWait { get; set; } = 2;
    public bool IsFortress { get; set; }
    public bool IsFlare { get; set; }
    public bool IsMissileAttack { get; set; }
    public bool MayBouncingEnemy { get; set; }
    public int Cost { get; set; } = 9999;
    public int Cost1 { get; set; } = 9999;
    public int Cost2 { get; set; } = 9999;
    public Cell? Connection { get; set; }
    public double Weight { get; set; } = 1;
    public bool IsSea => !(IsLand || IsEnemy || IsSiren || IsFortress || IsBoss);
    public bool MayCarrier => IsSea && !MayEnemy;
    public bool IsAccessible => Cost < 9999;
    public bool IsAccessible1 => Cost1 < 9999;
    public bool IsAccessible2 => Cost2 < 9999;
    public bool IsNearby => Cost < 20;

    public CellState(Cell location, MapTile declaration)
    {
        if (location.Column < 1 || location.Row < 1) throw new ArgumentOutOfRangeException(nameof(location));
        Location = location;
        LoadDeclaration(declaration);
    }

    // Reloading loop declarations does not wipe observed state, like GridInfo.decode.
    public void LoadDeclaration(MapTile tile)
    {
        if (!Enum.IsDefined(tile)) throw new ArgumentOutOfRangeException(nameof(tile));
        IsLand = tile == MapTile.Land;
        IsSpawnPoint = tile == MapTile.Spawn;
        IsSubmarineSpawnPoint = tile == MapTile.SubmarineSpawn;
        MayEnemy = tile is MapTile.Enemy or MapTile.LowPriorityEnemy;
        MayBoss = tile == MapTile.Boss;
        MayMystery = tile == MapTile.Mystery;
        MayAmmo = tile == MapTile.Ammo;
        MaySiren = tile == MapTile.Siren;
        MayAmbush = !(MayEnemy || MayBoss || MayMystery);
    }

    public bool Merge(CellObservation info, MapScanMode mode = MapScanMode.Normal,
        MapGridBehavior gridBehavior = MapGridBehavior.Default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (info.IsSubmarine && IsSubmarineSpawnPoint) IsSubmarine = true;
        if (info.IsCaughtBySiren)
        {
            if (!IsSea) return false;
            IsFleet = true; IsCaughtBySiren = true;
        }
        if (info.IsFleet)
        {
            if (!IsSea) return false;
            IsFleet = true;
            if (info.IsCurrentFleet) IsCurrentFleet = true;
            if (!(mode == MapScanMode.Init && info.IsEnemy)) return true;
        }
        if (info.IsBoss && gridBehavior == MapGridBehavior.W15)
        {
            if (!IsLand && MaySiren)
            {
                IsSiren = true;
                EnemyScale = 0;
                EnemyGenre = string.Empty;
                return true;
            }
        }
        if (info.IsBoss)
        {
            if (IsLand || !MayBoss) return false;
            IsBoss = true;
            return true;
        }
        if (info.IsSiren)
        {
            if (IsLand || !(MaySiren || mode == MapScanMode.Movable || IsMovable)) return false;
            IsSiren = true; EnemyScale = 0; EnemyGenre = info.EnemyGenre;
            return true;
        }
        if (info.IsEnemy)
        {
            if (IsFortress) return true;
            if (!IsLand && (MayEnemy || IsCarrier || mode == MapScanMode.Decoy))
            {
                IsEnemy = true;
                if (info.EnemyScale != 0 && EnemyScale == 0 || info.EnemyScale == 3 && EnemyScale == 2) EnemyScale = info.EnemyScale;
            }
            else if (mode == MapScanMode.Carrier && !IsLand && MayCarrier)
            {
                IsEnemy = true; IsCarrier = true;
                if (info.EnemyScale != 0) EnemyScale = info.EnemyScale;
            }
            else if ((mode == MapScanMode.Movable || IsMovable) && !IsLand)
            {
                IsEnemy = true;
                if (info.EnemyScale != 0) EnemyScale = info.EnemyScale;
            }
            else return false;
            if (!string.IsNullOrEmpty(info.EnemyGenre) && !(info.EnemyGenre == "Enemy" && !string.IsNullOrEmpty(EnemyGenre)))
                EnemyGenre = info.EnemyGenre;
            return true;
        }
        if (info.IsMystery)
        {
            if (!MayMystery) return false;
            IsMystery = true; return true;
        }
        if (info.IsAmmo)
        {
            if (!MayAmmo) return false;
            IsAmmo = true; return true;
        }
        if (info.IsMissileAttack)
        {
            if (MaySiren) IsSiren = true;
            else if (MayEnemy) IsEnemy = true;
        }
        return true;
    }

    public void WipeOut()
    {
        IsEnemy = false; EnemyScale = 0; EnemyGenre = null; IsMystery = false; IsBoss = false;
        IsAmmo = false; IsSiren = false; IsFortress = false; IsCaughtBySiren = false; IsCarrier = false; IsMovable = false;
        if (IsMechanismTrigger)
        {
            var triggers = MechanismTrigger ?? throw new InvalidOperationException("Mechanism trigger group is missing");
            foreach (var cell in triggers) cell.IsMechanismTrigger = false;
            var blocks = MechanismBlock ?? throw new InvalidOperationException("Mechanism block group is missing");
            foreach (var cell in blocks) cell.IsMechanismBlock = false;
        }
    }
    public void Reset()
    {
        WipeOut();
        IsFleet = false; IsCurrentFleet = false; IsSubmarine = false; IsCleared = false;
        IsMechanismTrigger = false; IsMechanismBlock = false;
        MechanismTrigger = null; MechanismBlock = null; MayBouncingEnemy = false;
    }
    public IReadOnlyList<(int X, int Y)> CoveredOffsets()
        => IsCurrentFleet ? [(0, -1), (0, -2)] : IsFleet || IsSiren || IsMystery ? [(0, -1)] : [];
    public int DistanceTo(CellState other) => Math.Abs(Location.Column - other.Location.Column) + Math.Abs(Location.Row - other.Location.Row);
    public string Encode()
    {
        if (IsLand) return "++";
        if (IsBoss) return "BO";
        if (IsSiren)
        {
            if (string.IsNullOrEmpty(EnemyGenre)) return "SU";
            string name = EnemyGenre.Length > 6 ? EnemyGenre[6..] : "";
            int separator = name.IndexOf('_');
            if (separator >= 0) name = name[(separator + 1)..];
            name = name.Length > 2 ? name[..2] : name;
            return name.Length == 0 ? "SU" : name.Length == 1 ? name.ToUpperInvariant() + " " : name.ToUpperInvariant();
        }
        if (IsEnemy) return EnemyScale.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            (string.IsNullOrEmpty(EnemyGenre) ? "E" : char.ToUpperInvariant(EnemyGenre[0]).ToString());
        if (IsCurrentFleet) return "FL";
        if (IsCaughtBySiren) return "Fc";
        if (IsFleet) return "Fl";
        if (IsSubmarine) return "ss";
        if (IsMystery) return "MY";
        if (IsAmmo) return "AM";
        if (IsFortress) return "FR";
        if (IsMissileAttack) return "MI";
        if (MayBouncingEnemy) return "BE";
        return IsCleared ? "==" : "--";
    }
    public override string ToString() => Location.ToString();
    // GridInfo.merge mutates scalar fields only. Native Map.update uses copy.copy per observation;
    // shared mechanism references are preserved and never modified by this preflight operation.
    internal CellState CopyForObservation() => (CellState)MemberwiseClone();
    public bool Equals(CellState? other) => other is not null && Location == other.Location;
    public override bool Equals(object? obj) => obj is CellState other && Equals(other);
    public override int GetHashCode() => Location.GetHashCode();
    public static bool operator ==(CellState? left, CellState? right) => Equals(left, right);
    public static bool operator !=(CellState? left, CellState? right) => !Equals(left, right);
}
