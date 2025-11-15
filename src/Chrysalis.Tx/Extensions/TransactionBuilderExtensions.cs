using Chrysalis.Cbor.Extensions;
using Chrysalis.Cbor.Extensions.Cardano.Core.Common;
using Chrysalis.Cbor.Extensions.Cardano.Core.Transaction;
using Chrysalis.Cbor.Serialization;
using Chrysalis.Cbor.Types;
using Chrysalis.Cbor.Types.Cardano.Core.Common;
using Chrysalis.Cbor.Types.Cardano.Core.Protocol;
using Chrysalis.Cbor.Types.Cardano.Core.Transaction;
using Chrysalis.Cbor.Types.Cardano.Core.TransactionWitness;
using Chrysalis.Plutus.VM.EvalTx;
using Chrysalis.Tx.Builders;
using Chrysalis.Tx.Models;
using Chrysalis.Tx.Models.Cbor;
using Chrysalis.Tx.Utils;
using Chrysalis.Wallet.Models.Enums;
using WalletAddress = Chrysalis.Wallet.Models.Addresses.Address;

namespace Chrysalis.Tx.Extensions;

public static class TransactionBuilderExtensions
{
    private static bool IsVKeyPaymentAddress(TransactionOutput output)
    {
        WalletAddress addr = output switch
        {
            AlonzoTransactionOutput alonzo => WalletAddress.FromBytes(alonzo.Address.Value),
            PostAlonzoTransactionOutput postAlonzo => WalletAddress.FromBytes(postAlonzo.Address.Value),
            _ => throw new Exception("Invalid transaction output type")
        };

        return addr.Type is AddressType.Base
            or AddressType.EnterprisePayment
            or AddressType.PaymentWithPointerDelegation
            or AddressType.PaymentWithScriptDelegation == false
            && addr.Type is not AddressType.ScriptPaymentWithDelegation
            && addr.Type is not AddressType.ScriptPaymentWithPointerDelegation
            && addr.Type is not AddressType.ScriptPaymentWithScriptDelegation
            && addr.Type is not AddressType.EnterpriseScriptPayment;
    }

    private static TransactionOutput BuildReturnOutput(Value value, TransactionBuilder builder, ResolvedInput firstCollateral)
    {
        if (builder.CollateralReturnAddress is not null)
        {
            return new PostAlonzoTransactionOutput(new Chrysalis.Cbor.Types.Cardano.Core.Common.Address(builder.CollateralReturnAddress.ToBytes()), value, null, null);
        }

        return firstCollateral.Output switch
        {
            AlonzoTransactionOutput alonzo => new AlonzoTransactionOutput(alonzo.Address, value, null),
            PostAlonzoTransactionOutput postAlonzo => new AlonzoTransactionOutput(postAlonzo.Address!, value, null),
            _ => throw new Exception("Invalid transaction output type")
        };
    }
    public static TransactionBuilder CalculateFee(this TransactionBuilder builder, List<Script> scripts, ulong defaultFee = 0, int mockWitnessFee = 1, List<ResolvedInput>? availableInputs = null)
    {

        ulong scriptFee = 0;
        ulong scriptExecutionFee = 0;
        if (builder.witnessSet.Redeemers is not null)
        {
            builder.SetTotalCollateral(2000000UL);

            if (scripts.Count <= 0)
            {
                throw new ArgumentNullException(nameof(scripts), "Missing script");
            }

            CborMaybeIndefList<long> usedLanguage = builder.pparams!.CostModelsForScriptLanguage!.Value[scripts[0].Version() - 1];
            CostMdls costModel = new(new Dictionary<int, CborMaybeIndefList<long>>(){
                 { scripts[0].Version() - 1, usedLanguage }
            });
            byte[] costModelBytes = CborSerializer.Serialize(costModel);
            byte[] scriptDataHash = DataHashUtil.CalculateScriptDataHash(builder.witnessSet.Redeemers, builder.witnessSet.PlutusDataSet?.GetValue() as PlutusList, costModelBytes);
            builder.SetScriptDataHash(scriptDataHash);

            ulong scriptCostPerByte = builder.pparams!.MinFeeRefScriptCostPerByte!.Numerator / builder.pparams.MinFeeRefScriptCostPerByte!.Denominator;
            scriptFee = (ulong)scripts.Sum(script => (decimal)FeeUtil.CalculateReferenceScriptFee(script.Bytes(), scriptCostPerByte));

            RationalNumber memUnitsCost = new(builder.pparams!.ExecutionCosts!.MemPrice!.Numerator!, builder.pparams.ExecutionCosts!.MemPrice!.Denominator!);
            RationalNumber stepUnitsCost = new(builder.pparams.ExecutionCosts!.StepPrice!.Numerator!, builder.pparams.ExecutionCosts!.StepPrice!.Denominator!);
            scriptExecutionFee = FeeUtil.CalculateScriptExecutionFee(builder.witnessSet.Redeemers, memUnitsCost, stepUnitsCost);

        }

        builder.SetFee(defaultFee == 0 ? 2000000UL : defaultFee);

        // Recursive fee and collateral calculation
        // Since adding collateral inputs changes transaction size, which changes fee, which changes collateral requirements
        ulong previousFee = 0;
        ulong fee = 0;
        int iterations = 0;
        int maxIterations = 10;

        while (iterations < maxIterations)
        {
            // Calculate current fee based on current transaction state
            Transaction draftTx = builder.Build();
            byte[] draftTxCborBytes = CborSerializer.Serialize(draftTx);
            ulong draftTxCborLength = (ulong)draftTxCborBytes.Length;

            fee = FeeUtil.CalculateFeeWithWitness(draftTxCborLength, builder.pparams!.MinFeeA!.Value, builder!.pparams.MinFeeB!.Value, mockWitnessFee) + scriptFee + scriptExecutionFee;

            // If fee hasn't changed significantly, we're done
            if (Math.Abs((long)fee - (long)previousFee) < 1000) // 1000 lovelace tolerance
            {
                break;
            }

            previousFee = fee;
            builder.SetFee(fee);

            // Handle collateral if needed
            if (builder.body.TotalCollateral is not null && availableInputs != null && availableInputs.Count > 0)
            {
                ulong totalCollateral = FeeUtil.CalculateRequiredCollateral(fee, builder.pparams!.CollateralPercentage!.Value);
                builder.SetTotalCollateral(totalCollateral);
                
                bool hasExplicitCollateral = builder.body.Collateral is not null && builder.body.Collateral.GetValue().Any();
                if (!hasExplicitCollateral)
                {
                    builder.body = builder.body with { Collateral = null, CollateralReturn = null };
                }

                TransactionOutput shapeOutput = availableInputs[0].Output;
                byte[] dummyReturnOutputBytes = CborSerializer.Serialize(shapeOutput);
                ulong estimatedMinLovelaceForReturn = FeeUtil.CalculateMinimumLovelace((ulong)builder.pparams!.AdaPerUTxOByte!, dummyReturnOutputBytes);

                ulong pad = builder.UseLegacyCollateralReturnPadding
                    ? (estimatedMinLovelaceForReturn / 2)
                    : builder.CollateralReturnPadLovelace;

                ulong totalCollateralNeeded = totalCollateral + estimatedMinLovelaceForReturn + pad;

                int maxCollateralInputs = (int)(builder.pparams!.MaxCollateralInputs ?? 3);
                
                // Build lookup for available inputs
                var utxoLookup = new Dictionary<(byte[] TxId, ulong Index), ResolvedInput>(new TransactionTemplateBuilder<object>.TransactionInputEqualityComparer());
                foreach (ResolvedInput u in availableInputs)
                {
                    utxoLookup[(u.Outref.TransactionId, u.Outref.Index)] = u;
                }

                // spending inputs from body
                List<ResolvedInput> spendingInputs = new();
                foreach (TransactionInput input in builder.body.Inputs.GetValue())
                {
                    var key = (input.TransactionId, input.Index);
                    if (utxoLookup.TryGetValue(key, out var res)) spendingInputs.Add(res);
                }

                List<ResolvedInput> eligibleSpending = spendingInputs.Where(x => IsVKeyPaymentAddress(x.Output)).ToList();
                List<ResolvedInput> eligibleAll = availableInputs.Where(x => IsVKeyPaymentAddress(x.Output)).ToList();

                List<ResolvedInput> collateralInputs;
                if (hasExplicitCollateral)
                {
                    collateralInputs = new List<ResolvedInput>();
                    foreach (var ci in builder.body.Collateral!.GetValue())
                    {
                        var key = (ci.TransactionId, ci.Index);
                        if (utxoLookup.TryGetValue(key, out var res)) collateralInputs.Add(res);
                    }
                }
                else
                {
                    List<Value> collateralRequirement = [new Lovelace(totalCollateralNeeded)];
                    try
                    {
                        collateralInputs = CoinSelectionUtil.LargestFirstAlgorithm(eligibleSpending, collateralRequirement, maxCollateralInputs).Inputs;
                    }
                    catch (InvalidOperationException)
                    {
                        try
                        {
                            collateralInputs = CoinSelectionUtil.LargestFirstAlgorithm(eligibleAll, collateralRequirement, maxCollateralInputs).Inputs;
                        }
                        catch (InvalidOperationException)
                        {
                            collateralInputs = CoinSelectionUtil.LargestFirstAlgorithm(eligibleAll, [new Lovelace(totalCollateral)], maxCollateralInputs).Inputs;
                        }
                    }

                    foreach (ResolvedInput input in collateralInputs)
                    {
                        builder.AddCollateral(input.Outref);
                    }
                }

                // Calculate totals
                ulong totalCollateralInputLovelace = (ulong)collateralInputs.Sum(i => (long)i.Output.Amount().Lovelace());

                if (totalCollateralInputLovelace < totalCollateral)
                {
                    throw new InvalidOperationException(
                        $"Selected collateral inputs insufficient. Need {totalCollateral} lovelace but only have {totalCollateralInputLovelace} lovelace."
                    );
                }

                ulong returnLovelace = totalCollateralInputLovelace - totalCollateral;

                // Era-specific rule: In Alonzo, collateral must be ADA-only and there is no collateral return field.
                bool isAlonzoBody = builder.body is AlonzoTransactionBody;
                if (isAlonzoBody)
                {
                    // Validate ADA-only collateral
                    if (collateralInputs.Any(ci => ci.Output.Amount() is LovelaceWithMultiAsset))
                    {
                        throw new InvalidOperationException("Alonzo era requires ADA-only collateral inputs; token-bearing collateral is not allowed without CIP-40.");
                    }
                }

                // Aggregate all assets from collateral inputs
                Dictionary<byte[], TokenBundleOutput> aggregatedAssets = new(ByteArrayEqualityComparer.Instance);
                foreach (ResolvedInput input in collateralInputs)
                {
                    if (input.Output.Amount() is LovelaceWithMultiAsset multiAsset && multiAsset.MultiAsset?.Value != null)
                    {
                        foreach ((byte[] policyId, TokenBundleOutput tokenBundle) in multiAsset.MultiAsset.Value)
                        {
                            if (!aggregatedAssets.TryGetValue(policyId, out TokenBundleOutput? existingBundle))
                            {
                                aggregatedAssets[policyId] = tokenBundle;
                            }
                            else
                            {
                                // Merge token bundles
                                Dictionary<byte[], ulong> mergedTokens = new(ByteArrayEqualityComparer.Instance);
                                foreach ((byte[] name, ulong amount) in existingBundle.Value)
                                {
                                    mergedTokens[name] = amount;
                                }
                                foreach ((byte[] name, ulong amount) in tokenBundle.Value)
                                {
                                    mergedTokens[name] = mergedTokens.TryGetValue(name, out ulong existing) ? existing + amount : amount;
                                }
                                aggregatedAssets[policyId] = new TokenBundleOutput(mergedTokens);
                            }
                        }
                    }
                }

                // Build return value
                Value returnValue;
                if (aggregatedAssets.Count > 0)
                {
                    returnValue = new LovelaceWithMultiAsset(
                        new Lovelace(returnLovelace),
                        new MultiAssetOutput(aggregatedAssets)
                    );
                }
                else
                {
                    returnValue = new Lovelace(returnLovelace);
                }

                // Create return output using preferred address or first collateral's address
                ResolvedInput firstCollateral = collateralInputs[0];
                TransactionOutput returnOutput = BuildReturnOutput(returnValue, builder, firstCollateral);

                // Final verification of minimum ADA requirement
                byte[] returnOutputBytes = CborSerializer.Serialize(returnOutput);
                ulong minLovelaceRequired = FeeUtil.CalculateMinimumLovelace(
                    (ulong)builder.pparams!.AdaPerUTxOByte!,
                    returnOutputBytes
                );

                if (returnLovelace < minLovelaceRequired)
                {
                    throw new InvalidOperationException(
                        $"Collateral return output ({returnLovelace} lovelace) is below minimum ADA requirement ({minLovelaceRequired} lovelace). Available inputs cannot provide sufficient collateral with adequate return."
                    );
                }

                // If collateral has tokens or if caller set a preferred address, set CIP-40 fields (Babbage/Conway)
                bool hasTokens = aggregatedAssets.Count > 0;
                // Only set CIP-40 fields in Babbage/Conway; Alonzo must remain without them
                bool isPostAlonzoBody = builder.body is BabbageTransactionBody || builder.body is ConwayTransactionBody;
                if (isPostAlonzoBody && (hasTokens || builder.CollateralReturnAddress is not null))
                {
                    builder.SetCollateralReturn(returnOutput);
                }
            }

            iterations++;
        }

        if (iterations >= maxIterations)
        {
            throw new InvalidOperationException("Fee calculation did not converge after maximum iterations.");
        }

        builder.SetFee(fee);

        if (builder.changeOutput is null)
        {
            return builder;
        }

        List<TransactionOutput> outputs = [.. builder.body.Outputs.GetValue()];

        decimal updatedChangeLovelace = builder.changeOutput switch
        {
            AlonzoTransactionOutput alonzo => alonzo.Amount switch
            {
                Lovelace value => (decimal)value.Value - fee,
                LovelaceWithMultiAsset multiAsset => multiAsset.LovelaceValue.Value - fee,
                _ => throw new Exception("Invalid change output type")
            },
            PostAlonzoTransactionOutput postAlonzoChange => postAlonzoChange.Amount switch
            {
                Lovelace value => value.Value - fee,
                LovelaceWithMultiAsset multiAsset => multiAsset.LovelaceValue.Value - fee,
                _ => throw new Exception("Invalid change output type")
            },
            _ => throw new Exception("Invalid change output type")
        };

        if (updatedChangeLovelace < 0)
        {
            updatedChangeLovelace = 0;
        }

        Value changeValue = new Lovelace((ulong)updatedChangeLovelace);

        Value changeOutputValue = builder.changeOutput switch
        {
            AlonzoTransactionOutput alonzo => alonzo.Amount,
            PostAlonzoTransactionOutput postAlonzoChange => postAlonzoChange.Amount,
            _ => throw new Exception("Invalid change output type")
        };

        if (changeOutputValue is LovelaceWithMultiAsset lovelaceWithMultiAsset)
        {
            changeValue = new LovelaceWithMultiAsset(new Lovelace((ulong)updatedChangeLovelace), lovelaceWithMultiAsset.MultiAsset);
        }

        TransactionOutput? updatedChangeOutput = null;

        if (updatedChangeLovelace > 0)
        {
            if (builder.changeOutput is AlonzoTransactionOutput change)
            {
                updatedChangeOutput = new AlonzoTransactionOutput(
                    change.Address,
                    changeValue,
                    change.DatumHash
                );

            }
            else if (builder.changeOutput is PostAlonzoTransactionOutput postAlonzoChange)
            {
                updatedChangeOutput = new PostAlonzoTransactionOutput(
                    postAlonzoChange.Address!,
                    changeValue,
                    postAlonzoChange.Datum,
                    postAlonzoChange.ScriptRef
                );
            }
        }

        outputs.RemoveAt(outputs.Count - 1);

        if (updatedChangeOutput is not null)
        {
            byte[] updatedChangeOutputBytes = CborSerializer.Serialize(updatedChangeOutput);
            ulong minLovelace = FeeUtil.CalculateMinimumLovelace((ulong)builder.pparams!.AdaPerUTxOByte!, updatedChangeOutputBytes);

            if (updatedChangeLovelace < minLovelace)
            {
                builder.SetFee(fee + (ulong)updatedChangeLovelace);
            }
            else
            {
                outputs.Add(updatedChangeOutput);
            }
        }

        builder.SetOutputs(outputs);

        return builder;
    }

    public static TransactionBuilder Evaluate(this TransactionBuilder builder, List<ResolvedInput> utxos, NetworkType networkType)
    {
        CborDefList<ResolvedInput> utxoCbor = new(utxos);
        byte[] utxoCborBytes = CborSerializer.Serialize<CborMaybeIndefList<ResolvedInput>>(utxoCbor);
        Transaction transaction = builder.Build();
        byte[] txCborBytes = CborSerializer.Serialize(transaction);
        IReadOnlyList<Plutus.VM.Models.EvaluationResult> evalResult = Evaluator.EvaluateTx(txCborBytes, utxoCborBytes, networkType);
        Redeemers? previousRedeemers = builder.witnessSet.Redeemers;


        switch (previousRedeemers)
        {
            case RedeemerList redeemersList:
                List<RedeemerEntry> updatedRedeemersList = [];
                foreach (RedeemerEntry redeemer in redeemersList.Value)
                {
                    foreach (Plutus.VM.Models.EvaluationResult result in evalResult)
                    {
                        if (redeemer.Tag == (int)result.RedeemerTag && redeemer.Index == result.Index)
                        {
                            ExUnits exUnits = new(result.ExUnits.Mem, result.ExUnits.Steps);
                            updatedRedeemersList.Add(new RedeemerEntry(redeemer.Tag, redeemer.Index, redeemer.Data, exUnits));
                        }
                    }
                }
                builder.SetRedeemers(new RedeemerList(updatedRedeemersList));
                break;
            case RedeemerMap redeemersMap:
                Dictionary<RedeemerKey, RedeemerValue> updatedRedeemersMap = [];
                foreach (KeyValuePair<RedeemerKey, RedeemerValue> kvp in redeemersMap.Value)
                {
                    foreach (Plutus.VM.Models.EvaluationResult result in evalResult)
                    {
                        if (kvp.Key.Tag == (int)result.RedeemerTag && kvp.Key.Index == result.Index)
                        {
                            ExUnits exUnits = new(result.ExUnits.Mem, result.ExUnits.Steps);
                            updatedRedeemersMap.Add(new RedeemerKey(kvp.Key.Tag, kvp.Key.Index), new RedeemerValue(kvp.Value.Data, exUnits));
                        }
                    }
                }
                builder.SetRedeemers(new RedeemerMap(updatedRedeemersMap));
                break;
        }


        return builder;
    }
}