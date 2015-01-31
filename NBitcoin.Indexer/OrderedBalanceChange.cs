﻿using Microsoft.WindowsAzure.Storage.Table;
using NBitcoin.DataEncoders;
using NBitcoin.Indexer.DamienG.Security.Cryptography;
using NBitcoin.OpenAsset;
using NBitcoin.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NBitcoin.Indexer
{
    public class OrderedBalanceChange
    {
        public static IEnumerable<OrderedBalanceChange> ExtractScriptBalances(uint256 txId, Transaction transaction, uint256 blockId, BlockHeader blockHeader, int height)
        {
            if (transaction == null)
                throw new ArgumentNullException("transaction");
            if (txId == null)
                txId = transaction.GetHash();

            if (blockId == null && blockHeader != null)
                blockId = blockHeader.GetHash();

            Dictionary<Script, OrderedBalanceChange> changeByScriptPubKey = new Dictionary<Script, OrderedBalanceChange>();
            uint i = 0;
            foreach (var input in transaction.Inputs)
            {
                if (transaction.IsCoinBase)
                {
                    i++;
                    break;
                }
                var signer = input.ScriptSig.GetSigner();
                if (signer != null)
                {
                    OrderedBalanceChange entry = null;
                    if (!changeByScriptPubKey.TryGetValue(signer.ScriptPubKey, out entry))
                    {
                        entry = new OrderedBalanceChange(txId, signer.ScriptPubKey, blockId, blockHeader, height);
                        changeByScriptPubKey.Add(signer.ScriptPubKey, entry);
                    }
                    entry.SpentOutpoints.Add(input.PrevOut);
                    entry.SpentIndices.Add(i);
                }
                i++;
            }

            i = 0;
            bool hasOpReturn = false;
            foreach (var output in transaction.Outputs)
            {
                if (TxNullDataTemplate.Instance.CheckScriptPubKey(output.ScriptPubKey))
                {
                    hasOpReturn = true;
                    i++;
                    continue;
                }

                OrderedBalanceChange entry = null;
                if (!changeByScriptPubKey.TryGetValue(output.ScriptPubKey, out entry))
                {
                    entry = new OrderedBalanceChange(txId, output.ScriptPubKey, blockId, blockHeader, height);
                    changeByScriptPubKey.Add(output.ScriptPubKey, entry);
                }
                entry.ReceivedCoins.Add(new Coin()
                {
                    Outpoint = new OutPoint(txId, i),
                    TxOut = output
                });
                i++;
            }

            foreach (var entity in changeByScriptPubKey)
            {
                entity.Value.HasOpReturn = hasOpReturn;
                entity.Value.IsCoinbase = transaction.IsCoinBase;
            }

            return changeByScriptPubKey.Values;
        }

        public static IEnumerable<OrderedBalanceChange> ExtractWalletBalances(
                                                                            uint256 txId,
                                                                            Transaction tx,
                                                                            uint256 blockId,
                                                                            BlockHeader blockHeader,
                                                                            int height,
                                                                            WalletRuleEntryCollection walletCollection)
        {
            Dictionary<string, OrderedBalanceChange> entitiesByWallet = new Dictionary<string, OrderedBalanceChange>();
            var scriptBalances = ExtractScriptBalances(txId, tx, blockId, blockHeader, height);
            foreach (var scriptBalance in scriptBalances)
            {
                foreach (var walletRuleEntry in walletCollection.GetRulesFor(scriptBalance.ScriptPubKey))
                {
                    OrderedBalanceChange walletEntity = null;
                    if (!entitiesByWallet.TryGetValue(walletRuleEntry.WalletId, out walletEntity))
                    {
                        walletEntity = new OrderedBalanceChange(walletRuleEntry.WalletId, scriptBalance);
                        entitiesByWallet.Add(walletRuleEntry.WalletId, walletEntity);
                    }
                    walletEntity.Merge(scriptBalance, walletRuleEntry.Rule);
                }
            }
            foreach (var b in entitiesByWallet.Values)
                b.AddRedeemInfo();
            return entitiesByWallet.Values;
        }


        private readonly List<MatchedRule> _MatchedRules = new List<MatchedRule>();
        public List<MatchedRule> MatchedRules
        {
            get
            {
                return _MatchedRules;
            }
        }

        internal Task<bool> EnsureSpentCoinsLoadedAsync(uint256[] parentIds, Transaction[] transactions)
        {
            var repo = new NoSqlTransactionRepository();
            for (int i = 0 ; i < parentIds.Length ; i++)
            {
                if (transactions[i] == null)
                    return Task.FromResult(false);
                repo.Put(parentIds[i], transactions[i]);
            }
            return EnsureSpentCoinsLoadedAsync(repo);
        }

        public async Task<bool> EnsureSpentCoinsLoadedAsync(ITransactionRepository transactions)
        {
            if (SpentCoins != null)
                return true;
            CoinCollection result = new CoinCollection();
            for (int i = 0 ; i < SpentOutpoints.Count; i++)
            {
                var outpoint = SpentOutpoints[i];
                if (outpoint.IsNull)
                    continue;
                var prev = await transactions.GetAsync(outpoint.Hash).ConfigureAwait(false);
                if (prev == null)
                    return false;
                result.Add(new Coin(outpoint, prev.Outputs[SpentOutpoints[i].N]));
            }
            SpentCoins = result;
            AddRedeemInfo();
            return true;
        }

        internal void Merge(OrderedBalanceChange other, WalletRule walletRule)
        {
            if (other.ReceivedCoins.Count != 0)
            {
                ReceivedCoins.AddRange(other.ReceivedCoins);
                ReceivedCoins = new CoinCollection(ReceivedCoins.Distinct<Coin, OutPoint>(c => c.Outpoint));
                if (walletRule != null)
                    foreach (var c in other.ReceivedCoins)
                    {
                        this.MatchedRules.Add(new MatchedRule()
                        {
                            Index = c.Outpoint.N,
                            Rule = walletRule,
                            MatchType = MatchLocation.Output
                        });
                    }
            }

            if (other.SpentIndices.Count != 0)
            {
                SpentIndices.AddRange(other.SpentIndices);
                SpentIndices = SpentIndices.Distinct().ToList();

                SpentOutpoints.AddRange(other.SpentOutpoints);
                SpentOutpoints = SpentOutpoints.Distinct().ToList();

                //Remove cached value, no longer correct
                ColoredBalanceChangeEntry = null;
                SpentCoins = null;

                if (walletRule != null)
                    foreach (var c in other.SpentIndices)
                    {
                        this.MatchedRules.Add(new MatchedRule()
                        {
                            Index = c,
                            Rule = walletRule,
                            MatchType = MatchLocation.Input
                        });
                    }
            }
        }

        internal void AddRedeemInfo()
        {
            foreach (var match in MatchedRules)
            {
                var scriptRule = match.Rule as ScriptRule;
                if (scriptRule != null && scriptRule.RedeemScript != null)
                {
                    CoinCollection collection = match.MatchType == MatchLocation.Input ? SpentCoins : ReceivedCoins;
                    if (collection != null)
                    {
                        var outpoint = new OutPoint(TransactionId, match.Index);
                        var coin = collection[outpoint];
                        collection[outpoint] = new ScriptCoin(coin.Outpoint, coin.TxOut, scriptRule.RedeemScript);
                    }
                }
            }
        }

        BalanceId _BalanceId;
        public BalanceId BalanceId
        {
            get
            {
                return _BalanceId;
            }
            internal set
            {
                _BalanceId = value;
            }
        }

        public string PartitionKey
        {
            get
            {
                return BalanceId.PartitionKey;
            }
        }

        public int Height
        {
            get;
            set;
        }
        public uint256 BlockId
        {
            get;
            set;
        }
        public uint256 TransactionId
        {
            get;
            set;
        }
        public bool HasOpReturn
        {
            get;
            set;
        }

        public bool IsCoinbase
        {
            get;
            set;
        }

        public DateTime SeenUtc
        {
            get;
            set;
        }

        public OrderedBalanceChange()
        {
            _SpentIndices = new List<uint>();
            _SpentOutpoints = new List<OutPoint>();
            _ReceivedCoins = new CoinCollection();
        }
        private List<uint> _SpentIndices;
        public List<uint> SpentIndices
        {
            get
            {
                return _SpentIndices;
            }
            private set
            {
                _SpentIndices = value;
            }
        }

        private List<OutPoint> _SpentOutpoints;
        public List<OutPoint> SpentOutpoints
        {
            get
            {
                return _SpentOutpoints;
            }
            private set
            {
                _SpentOutpoints = value;
            }
        }

        private CoinCollection _ReceivedCoins;
        public CoinCollection ReceivedCoins
        {
            get
            {
                return _ReceivedCoins;
            }
            private set
            {
                _ReceivedCoins = value;
            }
        }


        private CoinCollection _SpentCoins;

        /// <summary>
        /// Might be null if parent transactions have not yet been indexed
        /// </summary>
        public CoinCollection SpentCoins
        {
            get
            {
                return _SpentCoins;
            }
            internal set
            {
                _SpentCoins = value;
            }
        }

        Money _Amount;
        public Money Amount
        {
            get
            {
                if (_Amount == null && _SpentCoins != null)
                {
                    _Amount = _ReceivedCoins.Select(c => c.Amount).Sum() - _SpentCoins.Select(c => c.Amount).Sum();
                }
                return _Amount;
            }
        }

        internal OrderedBalanceChange(DynamicTableEntity entity)
        {
            var splitted = entity.RowKey.Split(new string[] { "-" }, StringSplitOptions.RemoveEmptyEntries);
            Height = Helper.StringToHeight(splitted[1]);
            BalanceId = BalanceId.Parse(splitted[0]);

            var locator = BalanceLocator.Parse(string.Join("-", splitted.Skip(1).ToArray()), true);
            var confLocator = locator as ConfirmedBalanceLocator;
            if (confLocator != null)
            {
                Height = confLocator.Height;
                TransactionId = confLocator.TransactionId;
                BlockId = confLocator.BlockHash;
            }

            var unconfLocator = locator as UnconfirmedBalanceLocator;
            if (unconfLocator != null)
            {
                TransactionId = unconfLocator.TransactionId;
            }

            SeenUtc = entity.Properties["s"].DateTime.Value;

            _SpentOutpoints = Helper.DeserializeList<OutPoint>(Helper.GetEntityProperty(entity, "a"));

            if (entity.Properties.ContainsKey("b0"))
                _SpentCoins = new CoinCollection(Helper.DeserializeList<Spendable>(Helper.GetEntityProperty(entity, "b")).Select(s => new Coin(s)).ToList());
            else if (_SpentOutpoints.Count == 0)
                _SpentCoins = new CoinCollection();

            _SpentIndices = Helper.DeserializeList<IntCompactVarInt>(Helper.GetEntityProperty(entity, "ss")).Select(i => (uint)i.ToLong()).ToList();

            var receivedIndices = Helper.DeserializeList<IntCompactVarInt>(Helper.GetEntityProperty(entity, "c")).Select(i => (uint)i.ToLong()).ToList();
            var receivedTxOuts = Helper.DeserializeList<TxOut>(Helper.GetEntityProperty(entity, "d"));

            _ReceivedCoins = new CoinCollection();
            for (int i = 0 ; i < receivedIndices.Count ; i++)
            {
                _ReceivedCoins.Add(new Coin()
                {
                    Outpoint = new OutPoint(TransactionId, receivedIndices[i]),
                    TxOut = receivedTxOuts[i]
                });
            }

            var flags = entity.Properties["e"].StringValue;
            HasOpReturn = flags[0] == 'o';
            IsCoinbase = flags[1] == 'o';

            _MatchedRules = Helper.DeserializeObject<List<MatchedRule>>(entity.Properties["f"].StringValue).ToList();

            if (entity.Properties.ContainsKey("g"))
            {
                var ctx = new ColoredTransaction();
                ctx.FromBytes(entity["g"].BinaryValue);
                ColoredBalanceChangeEntry = new ColoredBalanceChangeEntry(this, ctx);
            }

            if (entity.Properties.ContainsKey("h"))
            {
                _Script = new Script(entity.Properties["h"].BinaryValue);
            }

            var data = Helper.GetEntityProperty(entity, "cu");
            if (data != null)
                CustomData = Encoding.UTF8.GetString(data);
        }

        internal OrderedBalanceChange(uint256 txId, Script scriptPubKey, uint256 blockId, BlockHeader blockHeader, int height)
            : this()
        {
            var balanceId = new BalanceId(scriptPubKey);
            Init(txId, balanceId, blockId, blockHeader, height);
            var scriptBytes = scriptPubKey.ToBytes(true);
            if (scriptPubKey.Length > BalanceId.MaxScriptSize)
            {
                _Script = scriptPubKey;
            }
        }

        private void Init(uint256 txId, BalanceId balanceId, uint256 blockId, BlockHeader blockHeader, int height)
        {
            BlockId = blockId;
            SeenUtc = blockHeader == null ? DateTime.UtcNow : blockHeader.BlockTime.UtcDateTime;
            Height = blockId == null ? int.MaxValue : height;
            TransactionId = txId;
            BalanceId = balanceId;
        }

        Script _Script;

        internal OrderedBalanceChange(uint256 txId, string walletId, Script scriptPubKey, uint256 blockId, BlockHeader blockHeader, int height)
            : this()
        {
            Init(txId, new BalanceId(walletId), blockId, blockHeader, height);
            _Script = scriptPubKey;
        }

        internal OrderedBalanceChange(string walletId, OrderedBalanceChange source)
            : this(source.TransactionId, walletId, source.ScriptPubKey, source.BlockId, null, source.Height)
        {
            SeenUtc = source.SeenUtc;
            IsCoinbase = source.IsCoinbase;
            HasOpReturn = source.HasOpReturn;
        }
        internal class IntCompactVarInt : CompactVarInt
        {
            public IntCompactVarInt(uint value)
                : base(value, 4)
            {
            }
            public IntCompactVarInt()
                : base(4)
            {

            }
        }

        public BalanceLocator CreateBalanceLocator()
        {
            if (Height == int.MaxValue)
                return new UnconfirmedBalanceLocator(SeenUtc, TransactionId);
            else
                return new ConfirmedBalanceLocator(this);
        }

        internal DynamicTableEntity ToEntity()
        {
            DynamicTableEntity entity = new DynamicTableEntity();
            entity.ETag = "*";
            entity.PartitionKey = PartitionKey;

            var locator = CreateBalanceLocator();
            entity.RowKey = BalanceId + "-" + locator.ToString(true);

            entity.Properties.Add("s", new EntityProperty(SeenUtc));
            Helper.SetEntityProperty(entity, "ss", Helper.SerializeList(SpentIndices.Select(e => new IntCompactVarInt(e))));

            Helper.SetEntityProperty(entity, "a", Helper.SerializeList(SpentOutpoints));
            if (SpentCoins != null)
                Helper.SetEntityProperty(entity, "b", Helper.SerializeList(SpentCoins.Select(c => new Spendable(c.Outpoint, c.TxOut))));
            Helper.SetEntityProperty(entity, "c", Helper.SerializeList(ReceivedCoins.Select(e => new IntCompactVarInt(e.Outpoint.N))));
            Helper.SetEntityProperty(entity, "d", Helper.SerializeList(ReceivedCoins.Select(e => e.TxOut)));
            var flags = (HasOpReturn ? "o" : "n") + (IsCoinbase ? "o" : "n");
            entity.Properties.AddOrReplace("e", new EntityProperty(flags));
            entity.Properties.AddOrReplace("f", new EntityProperty(Helper.Serialize(MatchedRules)));
            if (ColoredBalanceChangeEntry != null)
            {
                entity.Properties.AddOrReplace("g", new EntityProperty(ColoredBalanceChangeEntry._Colored.ToBytes()));
            }
            if (_Script != null)
            {
                entity.Properties.Add("h", new EntityProperty(_Script.ToBytes(true)));
            }
            if (CustomData != null)
            {
                Helper.SetEntityProperty(entity, "cu", Encoding.UTF8.GetBytes(CustomData));
            }
            return entity;
        }

        public string CustomData
        {
            get;
            set;
        }

        const string DateFormat = "yyyyMMddhhmmssff";
       

        public static IEnumerable<OrderedBalanceChange> ExtractScriptBalances(Transaction tx)
        {
            return ExtractScriptBalances(null, tx, null, null, 0);
        }
        
        internal Script ScriptPubKey
        {
            get
            {
                if (_Script == null)
                    _Script = BalanceId.ExtractScript();
                return _Script;
            }
        }
       

        public IEnumerable<WalletRule> GetMatchedRules(int index, MatchLocation matchType)
        {
            return MatchedRules.Where(r => r.Index == index && r.MatchType == matchType).Select(c => c.Rule);
        }


        public IEnumerable<WalletRule> GetMatchedRules(Coin coin)
        {
            return GetMatchedRules(coin.Outpoint);
        }

        public IEnumerable<WalletRule> GetMatchedRules(OutPoint outPoint)
        {
            if (outPoint.Hash == TransactionId)
                return GetMatchedRules((int)outPoint.N, MatchLocation.Output);
            else
            {
                var index = SpentOutpoints.IndexOf(outPoint);
                if (index == -1)
                    return new WalletRule[0];
                return GetMatchedRules((int)SpentIndices[index], MatchLocation.Input);
            }
        }

        public ColoredBalanceChangeEntry ColoredBalanceChangeEntry
        {
            get;
            set;
        }

        public bool MempoolEntry
        {
            get
            {
                return BlockId == null;
            }
        }



        public async Task<bool> EnsureColoredTransactionLoadedAsync(IColoredTransactionRepository repository)
        {
            if (ColoredBalanceChangeEntry != null)
                return true;
            var tx = await repository.Transactions.GetAsync(TransactionId).ConfigureAwait(false);
            if (tx == null)
                return false;
            try
            {
                var color = await tx.GetColoredTransactionAsync(repository).ConfigureAwait(false);
                if (color == null)
                    return false;
                ColoredBalanceChangeEntry = new ColoredBalanceChangeEntry(this, color);
                return true;
            }
            catch (TransactionNotFoundException)
            {
                return false;
            }
        }
    }
}
