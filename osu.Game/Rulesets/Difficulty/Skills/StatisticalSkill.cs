// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Utils;
using osu.Game.Rulesets.Mods;

namespace osu.Game.Rulesets.Difficulty.Skills
{
    public abstract class StatisticalSkill : Skill
    {
        /// <summary>
        /// Exponent that controls the rate of which decay increases as the index increases.
        /// Values closer to 1 decay faster whilst lower values give more weight to lower object difficulties.
        /// </summary>
        protected virtual double DecayExponent => 0.9;
        protected virtual double DecayWeight => 0.9;

        protected StatisticalSkill(Mod[] mods)
            : base(mods)
        {
        }

        /// <summary>
        /// Returns the difficulty value of the current <see cref="DifficultyHitObject"/>. This value is calculated with or without respect to previous objects.
        /// </summary>
        protected abstract double ObjectDifficultyOf(DifficultyHitObject current);

        protected sealed override double ProcessInternal(DifficultyHitObject current)
            => ObjectDifficultyOf(current);

        /// <summary>
        /// Transforms the object difficulties specifically for final difficulty summation.
        /// This can be used to decrease weight of certain notes based on a skill-specific criteria.
        /// </summary>
        protected virtual void ApplyDifficultyTransformation(double[] difficulties)
        {
        }

        public abstract override double DifficultyValue();

        /// <summary>
        /// Calculates the number of object difficulties weighted against the top object difficulty.
        /// </summary>
        public virtual double CountTopWeightedObjectDifficulties(double difficultyValue)
        {
            if (ObjectDifficulties.Count == 0)
                return 0.0;

            double consistentTopStrain = difficultyValue * (1 - DecayWeight); // What would the top strain be if all strain values were identical

            if (consistentTopStrain == 0)
                return 0.0;

            // Use a weighted sum of all strains. Constants are arbitrary and give nice values
            return ObjectDifficulties.Sum(s => DifficultyCalculationUtils.Logistic(s / consistentTopStrain, 0.88, 10, 1.1));
        }

        public static double DifficultyToPerformance(double difficulty) => 4.0 * Math.Pow(difficulty, 3.0);
    }
}
