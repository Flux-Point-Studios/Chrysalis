using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chrysalis.Cbor.Extensions;
using Chrysalis.Cbor.Extensions.Cardano.Core.Transaction;
using Chrysalis.Cbor.Serialization;
using Chrysalis.Cbor.Types;
using Chrysalis.Cbor.Types.Cardano.Core;
using Chrysalis.Cbor.Types.Cardano.Core.Common;
using Chrysalis.Cbor.Types.Cardano.Core.Protocol;
using Chrysalis.Cbor.Types.Cardano.Core.Transaction;
using Chrysalis.Network.Cbor.LocalStateQuery;
using Chrysalis.Tx.Builders;
using Chrysalis.Tx.Extensions;
using Chrysalis.Tx.Models;
using Chrysalis.Tx.Models.Cbor;
using Chrysalis.Tx.Utils;
using Chrysalis.Wallet.Models.Addresses;
using Chrysalis.Wallet.Models.Enums;
using Chrysalis.Wallet.Models.Keys;
using WalletAddress = Chrysalis.Wallet.Models.Addresses.Address;
using Xunit;

namespace Chrysalis.Test;

public class TxBehaviorTests
{
    private static ProtocolParams CreateTestParams() =>
        new(
            MinFeeA: 44UL,
            MinFeeB: 155381UL,
            MaxBlockBodySize: null,
            MaxTransactionSize: null,
            MaxBlockHeaderSize: null,
            KeyDeposit: null,
            PoolDeposit: null,
            MaximumEpoch: null,
            DesiredNumberOfStakePools: null,
            PoolPledgeInfluence: null,
            ExpansionRate: null,
            TreasuryGrowthRate: null,
            ProtocolVersion: null,
            MinPoolCost: null,
            AdaPerUTxOByte: 4310UL,
            CostModelsForScriptLanguage: new(new Dictionary<int, CborMaybeIndefList<long>> { { 0, new CborIndefList<long>(new List<long> { 1 }) } }),
            ExecutionCosts: new ExUnitPrices(new CborRationalNumber(577, 10000), new CborRationalNumber(721, 10000000)),
            MaxTxExUnits: null,
            MaxBlockExUnits: null,
            MaxValueSize: null,
            CollateralPercentage: 150UL,
            MaxCollateralInputs: 3UL,
            PoolVotingThresholds: null,
            DRepVotingThresholds: null,
            MinCommitteeSize: null,
            CommitteeTermLimit: null,
            GovernanceActionValidityPeriod: null,
            GovernanceActionDeposit: null,
            DRepDeposit: null,
            DRepInactivityPeriod: null,
            MinFeeRefScriptCostPerByte: new CborRationalNumber(1, 1)
        );

    [Fact]
    public void CalculateFee_ShouldNotUseDefaultFeeAsFinal()
    {
        ProtocolParams p = CreateTestParams();

        TransactionBuilder b = TransactionBuilder.Create(p);
        TransactionInput inp = new(HexStringCache.FromHexString("00"), 0);
        Chrysalis.Cbor.Types.Cardano.Core.Common.Address addr = new(new byte[29]);
        Value val = new Lovelace(2_000_000);
        TransactionOutput outp = new AlonzoTransactionOutput(addr, val, null);
        b.AddInput(inp).AddOutput(outp, true);

        ResolvedInput r = new(inp, outp);

        // Pass a tiny default fee and verify the algorithm computes a larger, correct fee instead of keeping the default
        b.CalculateFee(new List<Chrysalis.Cbor.Types.Cardano.Core.Common.Script>(), 1, 1, new List<ResolvedInput> { r });
        PostMaryTransaction tx = b.Build();

        Assert.NotEqual((ulong)1, tx.TransactionBody.Fee());
        Assert.True(tx.TransactionBody.Fee() > 0);
    }

    private sealed class FakeProvider : ICardanoDataProvider
    {
        private readonly Dictionary<string, List<ResolvedInput>> _byAddress;
        private readonly ProtocolParams _pparams;
        public NetworkType NetworkType { get; }

        public FakeProvider(Dictionary<string, List<ResolvedInput>> byAddress, ProtocolParams pparams, NetworkType networkType)
        {
            _byAddress = byAddress;
            _pparams = pparams;
            NetworkType = networkType;
        }

        public Task<List<ResolvedInput>> GetUtxosAsync(List<string> address)
        {
            List<ResolvedInput> acc = [];
            foreach (string a in address)
            {
                if (_byAddress.TryGetValue(a, out List<ResolvedInput>? list))
                {
                    acc.AddRange(list);
                }
            }
            return Task.FromResult(acc);
        }

        public Task<ProtocolParams> GetParametersAsync() => Task.FromResult(_pparams);

        public Task<string> SubmitTransactionAsync(Transaction tx) => Task.FromResult("tx_hash_dummy");

        public Task<Metadata?> GetTransactionMetadataAsync(string txHash) => Task.FromResult<Metadata?>(null);
    }

    [Fact]
    public async Task TemplateBuilder_NoSpuriousChangeFromFeeBuffer()
    {
        ProtocolParams p = CreateTestParams();

        // Construct simple test addresses
        byte[] pk = Enumerable.Repeat((byte)0x01, 32).ToArray();
        byte[] cc = Enumerable.Repeat((byte)0x02, 32).ToArray();
        WalletAddress sender = WalletAddress.FromPublicKeys(NetworkType.Testnet, AddressType.EnterprisePayment, new PublicKey(pk, cc));
        WalletAddress recipient = WalletAddress.FromPublicKeys(NetworkType.Testnet, AddressType.EnterprisePayment, new PublicKey(pk.Select(b => (byte)(b + 1)).ToArray(), cc));

        string senderBech32 = sender.ToBech32();
        string recipientBech32 = recipient.ToBech32();

        // Single UTXO of 8 ADA at the sender
        TransactionInput txIn = new(HexStringCache.FromHexString("00"), 0);
        TransactionOutput txOut = new AlonzoTransactionOutput(new Chrysalis.Cbor.Types.Cardano.Core.Common.Address(sender.ToBytes()), new Lovelace(8_000_000), null);
        ResolvedInput senderUtxo = new(txIn, txOut);

        var provider = new FakeProvider(
            new Dictionary<string, List<ResolvedInput>>
            {
                { senderBech32, [senderUtxo] }
            },
            p,
            NetworkType.Testnet
        );

        // Build a transaction: spend 3 ADA to recipient, sender is change address
        var tpl = TransactionTemplateBuilder<object>
            .Create(provider)
            .AddStaticParty("change", senderBech32, isChange: true)
            .AddStaticParty("to", recipientBech32, isChange: false)
            .AddInput((opt, _) =>
            {
                opt.From = "change";
                opt.UtxoRef = txIn;
                opt.Id = "0";
            })
            .AddOutput((opt, _, __) =>
            {
                opt.To = "to";
                opt.Amount = new Lovelace(3_000_000);
            });

        var build = tpl.Build(Eval: true);
        PostMaryTransaction tx = (PostMaryTransaction)await build(new object());

        // Expect exactly one output (recipient). No spurious change output derived from fee buffer.
        var outputs = tx.TransactionBody.Outputs().ToList();
        Assert.Single(outputs);

        // Additionally, ensure the lone output is to the recipient address
        string outAddr = outputs[0] switch
        {
            AlonzoTransactionOutput ao => WalletAddress.FromBytes(ao.Address.Value).ToBech32(),
            PostAlonzoTransactionOutput po => WalletAddress.FromBytes(po.Address!.Value).ToBech32(),
            _ => throw new InvalidOperationException("Unexpected output type")
        };
        Assert.Equal(recipientBech32, outAddr);
    }
}
