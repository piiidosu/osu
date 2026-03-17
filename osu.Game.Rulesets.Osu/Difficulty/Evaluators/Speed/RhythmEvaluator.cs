using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics.Tensors;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Osu.Difficulty.Preprocessing;
using osu.Game.Rulesets.Osu.Objects;

namespace osu.Game.Rulesets.Osu.Difficulty.Evaluators.Speed
{
    public static class RhythmEvaluator
    {
        private static readonly ConditionalWeakTable<DifficultyHitObject, ModelResult> inferenceCache = new();

        // Make sure this path is accurate to how you load the file!
        private static readonly Lazy<SafeOptimizedSSM> ssm = new(() =>
            new SafeOptimizedSSM(@"C:\ppdev\osu\osu.Game.Rulesets.Osu\Difficulty\Evaluators\Speed\rhythm_weights.bin")
        );

        public static double EvaluateDifficultyOf(DifficultyHitObject current)
        {
            var firstObject = current;
            while (firstObject.Previous(0) != null)
            {
                firstObject = firstObject.Previous(0);
            }

            if (!inferenceCache.TryGetValue(firstObject, out var modelResult))
            {
                modelResult = RunFullMapInference(firstObject);
                inferenceCache.Add(firstObject, modelResult);
            }

            if (modelResult.DhoToModelIndexMap.TryGetValue(current.Index, out int tokenIndex))
            {
                if (tokenIndex == 0)
                    return 0;

                int targetDelta = modelResult.TimeDeltas[tokenIndex];
                int currentType = modelResult.ObjectTypes[tokenIndex];
                int prevType = modelResult.ObjectTypes[tokenIndex - 1];

                float prob;

                // Apply heuristic FIRST to bypass lookup if not needed
                if (targetDelta >= 511 || currentType == 3 || currentType == 4)
                {
                    prob = 1.0f;
                }
                else
                {
                    // Directly read the exact probability extracted from the model
                    float rawProb = modelResult.Probabilities[tokenIndex - 1];

                    if (prevType == 3 || prevType == 4)
                    {
                        prob = Math.Min(1.0f, rawProb * 2.0f);
                    }
                    else
                    {
                        prob = rawProb;
                    }
                }

                prob = Math.Max(prob, 1e-10f);
                return Math.Pow(Math.Max(-Math.Log10(prob) - 0.25, 0), 1.4);
            }

            return 0;
        }

        private static ModelResult RunFullMapInference(DifficultyHitObject firstObject)
        {
            var allObjects = new List<DifficultyHitObject>();
            var curr = firstObject;
            while (curr != null)
            {
                allObjects.Add(curr);
                curr = curr.Next(0);
            }

            var (objectTypes, timeDeltas, dhoToTokenMap) = GetObjectTokens(allObjects);
            int[] typesArray = objectTypes.ToArray();

            float[] probabilities = ssm.Value.RunInference(timeDeltas, typesArray, timeDeltas);

            return new ModelResult(probabilities, dhoToTokenMap, typesArray, timeDeltas);
        }

        private static (List<int> ObjectTypes, int[] TimeDeltas, Dictionary<int, int> Map) GetObjectTokens(IReadOnlyList<DifficultyHitObject> difficultyObjects)
        {
            List<int> objectTypes = new List<int>();
            List<int> timeDeltasList = new List<int>();
            Dictionary<int, int> dhoToModelIndexMap = new Dictionary<int, int>();

            foreach (var diffObject in difficultyObjects)
            {
                var baseObject = diffObject.BaseObject;
                dhoToModelIndexMap[diffObject.Index] = timeDeltasList.Count;

                double lastObjectEndDeltaTime = diffObject.Previous(0) != null
                    ? diffObject.StartTime - diffObject.Previous(0).EndTime
                    : 0;

                if (baseObject is HitCircle)
                {
                    objectTypes.Add(0); // Circle
                    timeDeltasList.Add((int)Math.Clamp(lastObjectEndDeltaTime, 0, 511));
                }
                else if (baseObject is Slider slider)
                {
                    objectTypes.Add(1); // Slider Head
                    timeDeltasList.Add((int)Math.Clamp(lastObjectEndDeltaTime, 0, 511));

                    objectTypes.Add(3); // Slider Tail
                    timeDeltasList.Add((int)Math.Clamp(diffObject.EndTime - diffObject.StartTime, 0, 511));
                }
                else if (baseObject is Spinner spinner)
                {
                    objectTypes.Add(4); // Spinner
                    timeDeltasList.Add((int)Math.Clamp(lastObjectEndDeltaTime + diffObject.EndTime - diffObject.StartTime, 0, 511));
                }
            }

            return (objectTypes, timeDeltasList.ToArray(), dhoToModelIndexMap);
        }

        private class ModelResult
        {
            public readonly float[] Probabilities;
            public readonly Dictionary<int, int> DhoToModelIndexMap;
            public readonly int[] ObjectTypes;
            public readonly int[] TimeDeltas;

            public ModelResult(float[] probs, Dictionary<int, int> map, int[] objectTypes, int[] timeDeltas)
            {
                Probabilities = probs;
                DhoToModelIndexMap = map;
                ObjectTypes = objectTypes;
                TimeDeltas = timeDeltas;
            }
        }
    }

    public class SafeOptimizedSSM
    {
        public const int VOCAB_SIZE = 512;
        private const int D_MODEL = 64;
        private const int LAYERS = 4;

        private readonly float[] _allWeights;

        private readonly ReadOnlyMemory<float> _embedding;
        private readonly LayerWeights[] _layers;
        private readonly ReadOnlyMemory<float> _headWeight;
        private readonly ReadOnlyMemory<float> _headBias;

        private struct LayerWeights
        {
            public ReadOnlyMemory<float> A_bar, B, C, D, NormW, NormB;
        }

        public SafeOptimizedSSM(string binPath)
        {
            byte[] bytes = File.ReadAllBytes(binPath);
            _allWeights = MemoryMarshal.Cast<byte, float>(bytes).ToArray();

            int offset = 0;
            _embedding = _allWeights.AsMemory(offset, VOCAB_SIZE * D_MODEL);
            offset += VOCAB_SIZE * D_MODEL;

            _layers = new LayerWeights[LAYERS];
            for (int i = 0; i < LAYERS; i++)
            {
                _layers[i] = new LayerWeights {
                    A_bar = _allWeights.AsMemory(offset, D_MODEL),
                    B = _allWeights.AsMemory(offset + D_MODEL, D_MODEL),
                    C = _allWeights.AsMemory(offset + D_MODEL * 2, D_MODEL),
                    D = _allWeights.AsMemory(offset + D_MODEL * 3, D_MODEL),
                    NormW = _allWeights.AsMemory(offset + D_MODEL * 4, D_MODEL),
                    NormB = _allWeights.AsMemory(offset + D_MODEL * 5, D_MODEL)
                };
                offset += D_MODEL * 6;
            }

            _headWeight = _allWeights.AsMemory(offset, VOCAB_SIZE * D_MODEL);
            offset += VOCAB_SIZE * D_MODEL;
            _headBias = _allWeights.AsMemory(offset, VOCAB_SIZE);
        }

        public float[] RunInference(int[] tokens, int[] objectTypes, int[] timeDeltas)
        {
            int seqLen = tokens.Length;

            // TARGET-ONLY EXTRACTION: Allocate exactly what we need
            float[] targetedProbs = new float[seqLen];

            // Standard safe stackalloc
            Span<float> hStates = stackalloc float[LAYERS * D_MODEL];
            Span<float> x = stackalloc float[D_MODEL];
            Span<float> logits = stackalloc float[VOCAB_SIZE];

            for (int t = 0; t < seqLen; t++)
            {
                _embedding.Span.Slice(tokens[t] * D_MODEL, D_MODEL).CopyTo(x);

                for (int l = 0; l < LAYERS; l++)
                {
                    ProcessLayer(l, x, hStates.Slice(l * D_MODEL, D_MODEL));
                }

                if (t + 1 < seqLen)
                {
                    int nextType = objectTypes[t + 1];
                    int nextDelta = timeDeltas[t + 1];

                    // PREDICTIVE SKIPPING: Skip heavy math if heuristic applies
                    if (nextDelta >= 511 || nextType == 3 || nextType == 4)
                    {
                        targetedProbs[t] = 1.0f;
                    }
                    else
                    {
                        GenerateProbabilities(x, logits);
                        targetedProbs[t] = logits[nextDelta];
                    }
                }
            }
            return targetedProbs;
        }

        private void ProcessLayer(int l, Span<float> x, Span<float> h)
        {
            var lw = _layers[l];
            ReadOnlySpan<float> a = lw.A_bar.Span;
            ReadOnlySpan<float> b = lw.B.Span;
            ReadOnlySpan<float> c = lw.C.Span;
            ReadOnlySpan<float> d = lw.D.Span;

            Span<float> temp = stackalloc float[D_MODEL];

            TensorPrimitives.Multiply(a, h, temp);
            TensorPrimitives.MultiplyAdd(b, x, temp, h);

            TensorPrimitives.Multiply(c, h, temp);
            TensorPrimitives.MultiplyAdd(d, x, temp, x);

            ApplyLayerNorm(x, lw.NormW.Span, lw.NormB.Span);
        }

        private void ApplyLayerNorm(Span<float> x, ReadOnlySpan<float> w, ReadOnlySpan<float> b)
        {
            Span<float> temp = stackalloc float[D_MODEL];

            float mean = TensorPrimitives.Sum(x) / D_MODEL;
            TensorPrimitives.Subtract(x, mean, temp);

            float var = TensorPrimitives.Dot(temp, temp) / D_MODEL;
            float invStd = 1.0f / (float)Math.Sqrt(var + 1e-5f);

            TensorPrimitives.Multiply(temp, invStd, temp);
            TensorPrimitives.Multiply(temp, w, temp);
            TensorPrimitives.Add(temp, b, x);
        }

        private void GenerateProbabilities(ReadOnlySpan<float> x, Span<float> probs)
        {
            ReadOnlySpan<float> hw = _headWeight.Span;
            ReadOnlySpan<float> hb = _headBias.Span;

            for (int i = 0; i < VOCAB_SIZE; i++)
            {
                ReadOnlySpan<float> row = hw.Slice(i * D_MODEL, D_MODEL);
                probs[i] = TensorPrimitives.Dot(x, row) + hb[i];
            }

            TensorPrimitives.SoftMax(probs, probs);
        }
    }
}
