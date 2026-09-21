using System;
using System.Collections.Generic;
using System.Text;
using Server.Items;
using Server.Spells;
using Server.Spells.Third;
using Server.Spells.Fourth;
using Server.Spells.Fifth;
using Server.Spells.Sixth;
using Server.Spells.Seventh;
using Server.Spells.Eighth;

namespace Server.Engines.Dueling
{
    public enum DuelWeapon
    {
        Any,
        Katana,
        Broadsword,
        VikingSword,
        Halberd,
        Fists
    }

    /// <summary>
    /// Rule set for a duel, parsed from a token string such as "5x-katana-nobandage".
    /// Tokens: 5x | 7x (skill cap), katana | broadsword | vikingsword | halberd | fists | any (weapon),
    /// magic (allow spellcasting), nobandage, noarmor. Separators: - , + / or whitespace. Default is "any".
    /// </summary>
    public class DuelRules
    {
        public static readonly SkillName[] CappedSkills =
        {
            SkillName.Swords, SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
            SkillName.MagicResist, SkillName.Parry, SkillName.Hiding, SkillName.Wrestling,
            SkillName.Magery, SkillName.EvalInt, SkillName.Meditation,
            SkillName.Fencing, SkillName.Macing, SkillName.Archery
        };

        public const string ValidTokens = "5x 7x katana broadsword vikingsword halberd fists any magic nobandage noarmor";

        // Classic 5x/7x templates also cap stats: Str + Dex + Int <= 225 with no single stat above 100.
        public const int StatTotalCap = 225;
        public const int StatSingleCap = 100;

        public int SkillCap { get; set; }          // 0 = unlimited, otherwise 500 / 700 (skill points, i.e. 50.0 / 70.0 per skill x10)
        public DuelWeapon Weapon { get; set; }
        public bool Magic { get; set; }            // spellcasting allowed while the round is live (still no fields/summons/travel/resurrection)
        public bool NoBandage { get; set; }
        public bool NoArmor { get; set; }

        public static DuelRules Default { get { return new DuelRules(); } }

        public static bool TryParse(string text, out DuelRules rules, out string error)
        {
            rules = new DuelRules();
            error = null;

            if (String.IsNullOrWhiteSpace(text))
                return true;

            string[] tokens = text.Split(new[] { '-', ',', '+', '/', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string raw in tokens)
            {
                string tok = raw.ToLowerInvariant();

                switch (tok)
                {
                    case "any": break;
                    case "5x": rules.SkillCap = 500; break;
                    case "7x": rules.SkillCap = 700; break;
                    case "katana": rules.Weapon = DuelWeapon.Katana; break;
                    case "broadsword": rules.Weapon = DuelWeapon.Broadsword; break;
                    case "vikingsword": rules.Weapon = DuelWeapon.VikingSword; break;
                    case "halberd": rules.Weapon = DuelWeapon.Halberd; break;
                    case "fists": rules.Weapon = DuelWeapon.Fists; break;
                    case "magic": rules.Magic = true; break;
                    case "nobandage": rules.NoBandage = true; break;
                    case "noarmor": rules.NoArmor = true; break;
                    default:
                        error = String.Format("Unknown rule token '{0}'. Valid: {1}", raw, ValidTokens);
                        return false;
                }
            }

            return true;
        }

        /// <summary>Canonical form, e.g. "5x-katana", "7x-nobandage", "any".</summary>
        public override string ToString()
        {
            var parts = new List<string>();

            if (SkillCap == 500) parts.Add("5x");
            else if (SkillCap == 700) parts.Add("7x");

            if (Weapon != DuelWeapon.Any)
                parts.Add(Weapon.ToString().ToLowerInvariant());

            if (Magic) parts.Add("magic");
            if (NoBandage) parts.Add("nobandage");
            if (NoArmor) parts.Add("noarmor");

            return parts.Count == 0 ? "any" : String.Join("-", parts);
        }

        public static double SkillTotal(Mobile m)
        {
            double total = 0.0;

            foreach (SkillName s in CappedSkills)
                total += m.Skills[s].Base;

            return total;
        }

        /// <summary>Returns false (with a reason) if the fighter's capped-skill total or raw stats exceed the 5x/7x template caps.</summary>
        public bool CheckSkills(Mobile m, out string reason)
        {
            reason = null;

            if (SkillCap <= 0)
                return true;

            string rule = String.Format("{0}x rule", SkillCap / 100);
            double total = SkillTotal(m);

            if (total > SkillCap)
            {
                reason = String.Format("{0}'s skills total {1:F1} > {2} ({3}).", m.Name, total, SkillCap, rule);
                return false;
            }

            int statTotal = m.RawStr + m.RawDex + m.RawInt;

            if (statTotal > StatTotalCap)
            {
                reason = String.Format("{0}'s stats total {1} > {2} ({3}).", m.Name, statTotal, StatTotalCap, rule);
                return false;
            }

            if (m.RawStr > StatSingleCap || m.RawDex > StatSingleCap || m.RawInt > StatSingleCap)
            {
                string stat = m.RawStr > StatSingleCap ? "Str" : (m.RawDex > StatSingleCap ? "Dex" : "Int");
                int value = m.RawStr > StatSingleCap ? m.RawStr : (m.RawDex > StatSingleCap ? m.RawDex : m.RawInt);

                reason = String.Format("{0}'s {1} {2} > {3} ({4}).", m.Name, stat, value, StatSingleCap, rule);
                return false;
            }

            return true;
        }

        /// <summary>Spells never allowed in the arena even under the magic rule: fields, summons, travel, resurrection.</summary>
        public static bool IsSpellBlocked(ISpell spell)
        {
            return spell is FireFieldSpell || spell is PoisonFieldSpell || spell is ParalyzeFieldSpell || spell is EnergyFieldSpell || spell is WallOfStoneSpell
                || spell is BladeSpiritsSpell || spell is EnergyVortexSpell || spell is SummonCreatureSpell || spell is SummonDaemonSpell
                || spell is AirElementalSpell || spell is EarthElementalSpell || spell is FireElementalSpell || spell is WaterElementalSpell
                || spell is RecallSpell || spell is GateTravelSpell || spell is MarkSpell || spell is TeleportSpell
                || spell is ResurrectionSpell;
        }

        public bool IsWeaponAllowed(Item item)
        {
            if (!(item is BaseWeapon))
                return true;

            switch (Weapon)
            {
                case DuelWeapon.Any: return true;
                case DuelWeapon.Fists: return false;
                case DuelWeapon.Katana: return item is Katana;
                case DuelWeapon.Broadsword: return item is Broadsword;
                case DuelWeapon.VikingSword: return item is VikingSword;
                case DuelWeapon.Halberd: return item is Halberd;
            }

            return true;
        }

        public bool IsArmorAllowed(Item item)
        {
            return !NoArmor || !(item is BaseArmor);
        }

        /// <summary>Returns null if the item may be worn under these rules, otherwise the rule token it violates.</summary>
        public string GetEquipViolation(Item item)
        {
            if (!IsWeaponAllowed(item))
                return Weapon.ToString().ToLowerInvariant();

            if (!IsArmorAllowed(item))
                return "noarmor";

            return null;
        }

        /// <summary>Moves every worn item that violates the rules into the fighter's backpack. Returns the items removed.</summary>
        public List<Item> EnforceEquipment(Mobile m)
        {
            var removed = new List<Item>();

            if (m == null || m.Deleted)
                return removed;

            var items = new List<Item>(m.Items);

            foreach (Item item in items)
            {
                if (item.Layer == Layer.Backpack || item.Layer == Layer.Mount || item.Layer == Layer.Bank || item.Layer == Layer.Hair || item.Layer == Layer.FacialHair)
                    continue;

                if (GetEquipViolation(item) == null)
                    continue;

                m.AddToBackpack(item);
                removed.Add(item);
            }

            return removed;
        }
    }
}
