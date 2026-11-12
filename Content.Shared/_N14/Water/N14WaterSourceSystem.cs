using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;

namespace Content.Shared._N14.Water;

/// <summary>
/// Handles entities that act as an infinite source of a reagent, see <see cref="N14WaterSourceComponent"/>.
/// </summary>
public sealed class N14WaterSourceSystem : EntitySystem
{
    [Dependency] private readonly SharedSolutionContainerSystem _solution = default!;

    /// <summary>
    /// Fills as much of <paramref name="target"/> as possible from an infinite water source.
    /// </summary>
    /// <returns>The amount of reagent that was actually added.</returns>
    public FixedPoint2 Fill(Entity<N14WaterSourceComponent?> source, Entity<SolutionComponent> target, FixedPoint2 amount)
    {
        if (!Resolve(source, ref source.Comp, false) || amount <= FixedPoint2.Zero)
            return FixedPoint2.Zero;

        var solution = target.Comp.Solution;
        var quantity = FixedPoint2.Min(amount, solution.AvailableVolume);

        if (quantity <= FixedPoint2.Zero)
            return FixedPoint2.Zero;

        solution.AddReagent(source.Comp.Reagent.Id, quantity);
        _solution.UpdateChemicals(target);

        return quantity;
    }
}
