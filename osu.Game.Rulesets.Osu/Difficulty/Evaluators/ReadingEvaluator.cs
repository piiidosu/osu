// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Utils;
using osu.Game.Rulesets.Osu.Difficulty.Preprocessing;
using osu.Game.Rulesets.Osu.Objects;

namespace osu.Game.Rulesets.Osu.Difficulty.Evaluators
{
    public static class ReadingEvaluator
    {
        private const double reading_window_size = 3000; // 3 seconds
        private const double distance_influence_threshold = OsuDifficultyHitObject.NORMALISED_DIAMETER * 1.5; // 1.5 circles distance between centers

        public static double EvaluateDifficultyOf(DifficultyHitObject current, bool hidden, bool traceable)
        {
            if (current.BaseObject is Spinner || current.Index == 0)
                return 0;

            var currObj = (OsuDifficultyHitObject)current;
            var nextObj = (OsuDifficultyHitObject)current.Next(0);
            var nextnextObj = (OsuDifficultyHitObject)current.Next(1);
            var prevObj = (OsuDifficultyHitObject)current.Previous(0);

            double velocity = Math.Max(1, currObj.LazyJumpDistance / currObj.AdjustedDeltaTime); // Only allow velocity to buff

            double currentVisibleObjectDensity = retrieveCurrentVisibleObjectDensity(currObj);
            double pastObjectDifficultyInfluence = getPastObjectDifficultyInfluence(currObj);

            double constantAngleNerfFactor = getConstantAngleNerfFactor(currObj);

            double noteDensityDifficulty = calculateDensityDifficulty(nextObj, velocity, constantAngleNerfFactor, pastObjectDifficultyInfluence, currentVisibleObjectDensity);

            double hiddenDifficulty = hidden
                ? calculateHiddenDifficulty(currObj, pastObjectDifficultyInfluence, currentVisibleObjectDensity, velocity, constantAngleNerfFactor)
                : 0;

            double traceableDifficulty = traceable
                ? calculateTraceableDifficulty(currObj, nextObj, nextnextObj, prevObj, pastObjectDifficultyInfluence, currentVisibleObjectDensity, velocity, constantAngleNerfFactor)
                : 0;

            double preemptDifficulty = calculatePreemptDifficulty(velocity, constantAngleNerfFactor, currObj.Preempt);

            double readingDifficulty = DiffUtils.Norm(1.5, preemptDifficulty, hiddenDifficulty, traceableDifficulty, noteDensityDifficulty);

            // Having less time to process information is harder
            readingDifficulty *= highBpmBonus(currObj.AdjustedDeltaTime);

            return readingDifficulty;
        }

        /// <summary>
        /// Calculates the density difficulty of the current object and how hard it is to aim it because of it based on:
        /// <list type="bullet">
        /// <item><description>cursor velocity to the current object,</description></item>
        /// <item><description>how many times the current object's angle was repeated,</description></item>
        /// <item><description>density of objects visible when the current object appears,</description></item>
        /// <item><description>density of objects visible when the current object needs to be clicked,</description></item>
        /// /// </list>
        /// </summary>
        private static double calculateDensityDifficulty(OsuDifficultyHitObject? nextObj, double velocity, double constantAngleNerfFactor,
                                                         double pastObjectDifficultyInfluence, double currentVisibleObjectDensity)
        {
            const double density_multiplier = 2.4;
            const double density_difficulty_base = 2.5;

            // Consider future densities too because it can make the path the cursor takes less clear
            double futureObjectDifficultyInfluence = Math.Sqrt(currentVisibleObjectDensity);

            if (nextObj != null)
            {
                // Reduce difficulty if movement to next object is small
                futureObjectDifficultyInfluence *= DiffUtils.Smootherstep(nextObj.LazyJumpDistance, 15, distance_influence_threshold);
            }

            // Value higher note densities exponentially
            double noteDensityDifficulty = DiffUtils.Pow(pastObjectDifficultyInfluence + futureObjectDifficultyInfluence, 1.7) * 0.4 * constantAngleNerfFactor * velocity;

            // Award only denser than average maps.
            noteDensityDifficulty = Math.Max(0, noteDensityDifficulty - density_difficulty_base);

            // Apply a soft cap to general density reading to account for partial memorization
            noteDensityDifficulty = DiffUtils.Pow(noteDensityDifficulty, 0.45) * density_multiplier;

            return noteDensityDifficulty;
        }

        /// <summary>
        /// Calculates the difficulty of aiming the current object when the approach rate is very high based on:
        /// <list type="bullet">
        /// <item><description>cursor velocity to the current object,</description></item>
        /// <item><description>how many times the current object's angle was repeated,</description></item>
        /// <item><description>how many milliseconds elapse between the approach circle appearing and touching the inner circle</description></item>
        /// </list>
        /// </summary>
        private static double calculatePreemptDifficulty(double velocity, double constantAngleNerfFactor, double preempt)
        {
            const double preempt_balancing_factor = 140000;
            const double preempt_starting_point = 500; // AR 9.66 in milliseconds

            // Arbitrary curve for the base value preempt difficulty should have as approach rate increases.
            // https://www.desmos.com/calculator/c175335a71
            double preemptDifficulty = DiffUtils.Pow((preempt_starting_point - preempt + Math.Abs(preempt - preempt_starting_point)) / 2, 2.5) / preempt_balancing_factor;

            preemptDifficulty *= constantAngleNerfFactor * velocity;

            return preemptDifficulty;
        }

        /// <summary>
        /// Calculates the difficulty of aiming the current object when the hidden mod is active based on:
        /// <list type="bullet">
        /// <item><description>cursor velocity to the current object,</description></item>
        /// <item><description>time the current object spends invisible,</description></item>
        /// <item><description>density of objects visible when the current object appears,</description></item>
        /// <item><description>density of objects visible when the current object needs to be clicked,</description></item>
        /// <item><description>how many times the current object's angle was repeated,</description></item>
        /// <item><description>if the current object is perfectly stacked to the previous one</description></item>
        /// </list>
        /// </summary>
        private static double calculateHiddenDifficulty(OsuDifficultyHitObject currObj, double pastObjectDifficultyInfluence, double currentVisibleObjectDensity, double velocity,
                                                        double constantAngleNerfFactor)
        {
            const double hidden_multiplier = 0.28;

            // Higher preempt means that time spent invisible is higher too, we want to reward that
            double preemptFactor = DiffUtils.Pow(currObj.Preempt, 2.2) * 0.01;

            // Account for both past and current densities
            double densityFactor = DiffUtils.Pow(currentVisibleObjectDensity + pastObjectDifficultyInfluence, 3.3) * 3;

            double hiddenDifficulty = (preemptFactor + densityFactor) * constantAngleNerfFactor * velocity * 0.01;

            // Apply a soft cap to general HD reading to account for partial memorization
            hiddenDifficulty = DiffUtils.Pow(hiddenDifficulty, 0.4) * hidden_multiplier;

            var previousObj = (OsuDifficultyHitObject)currObj.Previous(0);

            // Buff perfect stacks only if current note is completely invisible at the time you click the previous note.
            if (currObj.LazyJumpDistance == 0 && currObj.OpacityAt(previousObj.BaseObject.StartTime, true) == 0 && previousObj.StartTime > currObj.StartTime - currObj.Preempt)
                hiddenDifficulty += hidden_multiplier * 2500 / DiffUtils.Pow(currObj.AdjustedDeltaTime, 1.5); // Perfect stacks are harder the less time between notes

            return hiddenDifficulty;
        }

        private static double calculateTraceableDifficulty(OsuDifficultyHitObject currObj, OsuDifficultyHitObject nextObj, OsuDifficultyHitObject nextnextObj, OsuDifficultyHitObject prevObj, double pastObjectDifficultyInfluence, double currentVisibleObjectDensity, double velocity, double constantAngleNerfFactor)
        {
            const double traceable_multiplier = 1.4;

            // Account for both past and current densities
            // Reduce density difficulty for future linear movement
            double densityFactor = DiffUtils.Pow(DiffUtils.Pow(DiffUtils.Pow(currentVisibleObjectDensity, 0.9) + pastObjectDifficultyInfluence, 0.8), 3.3) * 3;

            double traceableDifficulty = 0.25 + (densityFactor * constantAngleNerfFactor * DiffUtils.Pow(velocity, 1.1) * 0.01);

            // Apply a soft cap to general TC reading to account for partial memorization
            traceableDifficulty = DiffUtils.Pow(traceableDifficulty, 0.36) * traceable_multiplier;

            double uncertainty = 1;
            double readingDifficulty = 1;

            // Slightly buff TC when the density is low, and there are no sliders in recent gameplay
            // This buffs TC when not enough are circles are consistently on the playfield to ensure consistent circle size memory
            if (prevObj != null)
            {
                // Add significant base difficulty if the first object after a break is a circle, or if it is after a very long spinner
                // Do not give additional bonuses if this is the case
                if (((currObj.AdjustedDeltaTime >= (currObj.Preempt + 2000))
                        || ((currObj.AdjustedDeltaTime + prevObj.AdjustedDeltaTime >= (currObj.Preempt + 2000)) && (prevObj.BaseObject is Spinner)))
                        && (currObj.BaseObject is HitCircle)
                        && (!(nextObj.BaseObject is Slider) || (nextObj.AdjustedDeltaTime >= nextObj.Preempt)))
                {
                    traceableDifficulty += 2.25;
                    return traceableDifficulty;
                }

                // Heavily decrease uncertainty if sliders were visible recently
                // Decrease uncertainty for each recent object
                if (nextObj != null)
                {
                    if (currObj.BaseObject is Slider || (prevObj.BaseObject is Slider && currentVisibleObjectDensity > 0) || nextObj.BaseObject is Slider)
                        uncertainty *= 0.25;
                }

                foreach (var loopObj in retrievePastVisibleObjects(currObj))
                {
                    double timeBetweenCurrAndLoopObj = currObj.StartTime - loopObj.StartTime;

                    if (loopObj.BaseObject is Slider)
                    {
                        uncertainty *= DiffUtils.Smootherstep(timeBetweenCurrAndLoopObj, Math.Min(1000, currObj.Preempt / 2), currObj.Preempt);
                        uncertainty *= 0.5;
                    }
                    else if (loopObj.BaseObject is HitCircle)
                    {
                        uncertainty *= DiffUtils.Smootherstep(timeBetweenCurrAndLoopObj, 0, currObj.Preempt * 0.75);
                        uncertainty *= 0.875;
                    }
                }
                uncertainty *= Math.Pow(0.99, currentVisibleObjectDensity);
            }

            // Buff TC when circles are close together such that the approach circles overlap.
            // Reduce the buff when the hitcircles are too close together or too far apart to be overlapping.
            if (nextObj != null)
            {
                // Calculates how much the following circle overlaps with the current one
                double nextCircleRadius = ((3 * (nextObj.AdjustedDeltaTime / nextObj.Preempt)) + 1) * 50;
                double futureOverlap = DiffUtils.Pow(Math.Max(0, nextCircleRadius + 75 - nextObj.JumpDistance), 0.3) * 0.8;

                // Reduce difficulty if movement to next object is small
                // Reduce difficulty if next object envelops current object
                double envelop_distance_tolerance = 20;
                if (nextCircleRadius + envelop_distance_tolerance - 50 < distance_influence_threshold)
                    futureOverlap *= DiffUtils.Smootherstep(nextObj.JumpDistance, nextCircleRadius + envelop_distance_tolerance - 50, distance_influence_threshold);
                futureOverlap *= DiffUtils.Smootherstep(nextObj.JumpDistance, 0, 50) / 4;

                // Reduce difficulty if objects are barely overlapping
                futureOverlap *= 1 - DiffUtils.Smootherstep(nextObj.JumpDistance - (nextCircleRadius + 50), 0, 20);

                // Reduce difficulty if the movement is close to linear
                double? currAngle = currObj.Angle;
                double? nextAngle = nextObj.Angle;

                double? maxAngle = (currAngle, nextAngle) switch
                {
                    (double curr, double next) => Math.Max(curr, next),
                    (double curr, null) => curr,
                    (null, double next) => next,
                    _ => null
                };

                if (maxAngle.HasValue)
                {
                    double angle = (double)maxAngle;
                    double linearity = DiffUtils.Smootherstep(angle, 170, 180);
                    futureOverlap *= 1 - DiffUtils.Smootherstep(angle, 150, 180) / 5 - (linearity / 10);

                    if (currAngle.HasValue && nextAngle.HasValue)
                    {
                        // Reduce difficulty if angles are similar
                        // Reduce difficulty if angles are wide and similar
                        double angleDifference = currAngle.Value - nextAngle.Value;
                        futureOverlap *= 1 - (DiffUtils.Smootherstep(Math.Abs(angleDifference), 0, 40) / 10) - (linearity / 10);
                        futureOverlap *= 1 - (DiffUtils.Smootherstep(Math.Abs(angleDifference), 0, 40) / 20 * (1 - DiffUtils.Smootherstep(angle, 150, 180))) - (linearity / 10);
                    }
                }
                // Increase difficulty for back and forth overlapping movement
                if ((nextnextObj != null) && (nextnextObj.Angle != null) && (nextObj != null) && (nextObj.Angle != null))
                {
                    double nextVelocityVector = nextObj.JumpDistance / Math.Max(nextObj.AdjustedDeltaTime, 10);
                    double nextnextVelocityVector = nextnextObj.JumpDistance / Math.Max(nextnextObj.AdjustedDeltaTime, 10);
                    double acuteAngleFactor = DiffUtils.Smootherstep(nextnextObj.Angle.Value, 0, 40) * DiffUtils.Smootherstep(nextObj.Angle.Value, 0, 40);

                    if ((nextnextVelocityVector == 0) || (nextVelocityVector == 0))
                    { }
                    else if (nextnextVelocityVector >= nextVelocityVector)
                        futureOverlap += 0.025 * ((1 - DiffUtils.Smootherstep(acuteAngleFactor, 0, 40)) * DiffUtils.Pow(nextVelocityVector / nextnextVelocityVector, 4)) * DiffUtils.Pow(velocity, 1.5);
                    else
                        futureOverlap += 0.025 * ((1 - DiffUtils.Smootherstep(acuteAngleFactor, 0, 40)) * DiffUtils.Pow(nextnextVelocityVector / nextVelocityVector, 4)) * DiffUtils.Pow(velocity, 1.5);
                }
                readingDifficulty += DiffUtils.Pow(futureOverlap, 0.8);
            }

            if (currObj.Preempt < 500)
                readingDifficulty *= 1 + DiffUtils.Pow((500 - currObj.Preempt) / 150, 2) / 10;

            if (currObj.BaseObject is Slider)
                readingDifficulty *= 0.8;

            return traceableDifficulty * readingDifficulty * (1 + uncertainty * 2);
        }

        private static double getPastObjectDifficultyInfluence(OsuDifficultyHitObject currObj)
        {
            double pastObjectDifficultyInfluence = 0;

            foreach (var loopObj in retrievePastVisibleObjects(currObj))
            {
                double loopDifficulty = currObj.OpacityAt(loopObj.BaseObject.StartTime, false);

                // When aiming an object small distances mean previous objects may be cheesed, so it doesn't matter whether they were arranged confusingly.
                loopDifficulty *= DiffUtils.Smootherstep(loopObj.LazyJumpDistance, 15, distance_influence_threshold);

                // Account less for objects close to the max reading window
                double timeBetweenCurrAndLoopObj = currObj.StartTime - loopObj.StartTime;
                double timeNerfFactor = getTimeNerfFactor(timeBetweenCurrAndLoopObj);

                loopDifficulty *= timeNerfFactor;
                pastObjectDifficultyInfluence += loopDifficulty;
            }

            return pastObjectDifficultyInfluence;
        }

        // Returns a list of objects that are visible on screen at the point in time the current object becomes visible.
        private static IEnumerable<OsuDifficultyHitObject> retrievePastVisibleObjects(OsuDifficultyHitObject current)
        {
            for (int i = 0; i < current.Index; i++)
            {
                OsuDifficultyHitObject hitObject = (OsuDifficultyHitObject)current.Previous(i);

                if (hitObject == null ||
                    current.StartTime - hitObject.StartTime > reading_window_size ||
                    hitObject.StartTime < current.StartTime - current.Preempt) // Current object not visible at the time object needs to be clicked
                    break;

                yield return hitObject;
            }
        }

        // Returns the density of objects visible at the point in time the current object needs to be clicked capped by the reading window.
        private static double retrieveCurrentVisibleObjectDensity(OsuDifficultyHitObject current)
        {
            double visibleObjectCount = 0;

            OsuDifficultyHitObject? hitObject = (OsuDifficultyHitObject)current.Next(0);

            while (hitObject != null)
            {
                if (hitObject.StartTime - current.StartTime > reading_window_size ||
                    current.StartTime < hitObject.StartTime - hitObject.Preempt) // Object not visible at the time current object needs to be clicked.
                    break;

                double timeBetweenCurrAndLoopObj = hitObject.StartTime - current.StartTime;
                double timeNerfFactor = getTimeNerfFactor(timeBetweenCurrAndLoopObj);

                visibleObjectCount += hitObject.OpacityAt(current.BaseObject.StartTime, false) * timeNerfFactor;

                hitObject = (OsuDifficultyHitObject?)hitObject.Next(0);
            }

            return visibleObjectCount;
        }

        // Returns a factor of how often the current object's angle has been repeated in a certain time frame.
        // It does this by checking the difference in angle between current and past objects and sums them based on a range of similarity.
        // https://www.desmos.com/calculator/eb057a4822
        private static double getConstantAngleNerfFactor(OsuDifficultyHitObject current)
        {
            const double minimum_angle_relevancy_time = 2000; // 2 seconds
            const double maximum_angle_relevancy_time = 200;

            double constantAngleCount = 0;
            int index = 0;
            double currentTimeGap = 0;

            OsuDifficultyHitObject loopObjPrev0 = current;
            OsuDifficultyHitObject? loopObjPrev1 = null;
            OsuDifficultyHitObject? loopObjPrev2 = null;

            while (currentTimeGap < minimum_angle_relevancy_time)
            {
                var loopObj = (OsuDifficultyHitObject)current.Previous(index);

                if (loopObj == null)
                    break;

                // Account less for objects that are close to the time limit.
                double longIntervalFactor = 1 - DiffUtils.ReverseLerp(loopObj.AdjustedDeltaTime, maximum_angle_relevancy_time, minimum_angle_relevancy_time);

                if (loopObj.Angle != null && current.Angle != null)
                {
                    double angleDifference = Math.Abs(current.Angle.Value - loopObj.Angle.Value);
                    double angleDifferenceAlternating = Math.PI;

                    if (loopObjPrev0.Angle != null && loopObjPrev1?.Angle != null && loopObjPrev2?.Angle != null)
                    {
                        angleDifferenceAlternating = Math.Abs(loopObjPrev1.Angle.Value - loopObj.Angle.Value);
                        angleDifferenceAlternating += Math.Abs(loopObjPrev2.Angle.Value - loopObjPrev0.Angle.Value);

                        double weight = 1.0;

                        // Be sure that one of the angles is very sharp, when other is wide
                        weight *= DiffUtils.ReverseLerp(Math.Min(loopObj.Angle.Value, loopObjPrev0.Angle.Value) * 180 / Math.PI, 20, 5);
                        weight *= DiffUtils.ReverseLerp(Math.Max(loopObj.Angle.Value, loopObjPrev0.Angle.Value) * 180 / Math.PI, 60, 120);

                        // Lerp between max angle difference and rescaled alternating difference, with more harsh scaling compared to normal difference
                        angleDifferenceAlternating = double.Lerp(Math.PI, 0.1 * angleDifferenceAlternating, weight);
                    }

                    double stackFactor = DiffUtils.Smootherstep(loopObj.LazyJumpDistance, 0, OsuDifficultyHitObject.NORMALISED_RADIUS);

                    constantAngleCount += Math.Cos(3 * Math.Min(double.DegreesToRadians(30), Math.Min(angleDifference, angleDifferenceAlternating) * stackFactor)) * longIntervalFactor;
                }

                currentTimeGap = current.StartTime - loopObj.StartTime;
                index++;

                loopObjPrev2 = loopObjPrev1;
                loopObjPrev1 = loopObjPrev0;
                loopObjPrev0 = loopObj;
            }

            return Math.Clamp(2 / constantAngleCount, 0.2, 1);
        }

        // Returns a nerfing factor for when objects are very distant in time, affecting reading less.
        private static double getTimeNerfFactor(double deltaTime)
        {
            return Math.Clamp(2 - deltaTime / (reading_window_size / 2), 0, 1);
        }

        private static double highBpmBonus(double ms) => 1 / (1 - DiffUtils.Pow(0.8, ms / 1000));
    }
}
