using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;

namespace DutyMechanic.Dungeons;

// Shelter selection uses actual surviving pillar circles and every active LOS cast.
// It does not follow party members or assume that a fixed compass corner is safe.
internal static class GarudaHardPolicy
{
    internal readonly record struct Pillar(Vector3 Position, float Radius);
    internal readonly record struct Cast(Vector3 Origin, float Range);

    internal static bool Covered(Vector3 point, IReadOnlyList<Pillar> pillars, IReadOnlyList<Cast> casts) =>
        casts.All(cast => point.Distance2D(cast.Origin) > cast.Range + 0.5f || pillars.Any(pillar =>
        {
            float dx = point.X - cast.Origin.X, dz = point.Z - cast.Origin.Z;
            float lengthSquared = dx * dx + dz * dz;
            if (lengthSquared < 0.01f || pillar.Radius <= 0.5f)
                return false;
            float t = ((pillar.Position.X - cast.Origin.X) * dx + (pillar.Position.Z - cast.Origin.Z) * dz) / lengthSquared;
            if (t <= 0 || t >= 1)
                return false;
            float x = cast.Origin.X + t * dx - pillar.Position.X;
            float z = cast.Origin.Z + t * dz - pillar.Position.Z;
            // Inset cover by half a yalm so arrival tolerance cannot expose a flank.
            float radius = pillar.Radius - 0.5f;
            return x * x + z * z <= radius * radius;
        }));

    internal static Vector3? SelectCover(Vector3 player, IReadOnlyList<Pillar> pillars,
        IReadOnlyList<Cast> casts, Func<Vector3, bool> safe)
    {
        if (safe(player))
            return player;
        var candidates = new List<Vector3>();
        foreach (var cast in casts)
        foreach (var pillar in pillars)
        {
            float dx = pillar.Position.X - cast.Origin.X, dz = pillar.Position.Z - cast.Origin.Z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance < 0.1f || pillar.Radius <= 0.5f)
                continue;
            // Stand one yalm beyond the pillar's collision radius along its shadow.
            float scale = (pillar.Radius + 1) / distance;
            candidates.Add(new(pillar.Position.X + dx * scale, pillar.Position.Y, pillar.Position.Z + dz * scale));
        }
        return candidates.Where(safe).OrderBy(p => p.Distance2D(player)).Select(p => (Vector3?)p).FirstOrDefault();
    }
}
