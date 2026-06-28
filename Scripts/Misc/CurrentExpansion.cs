#region References
using System;

using Server.Accounting;
using Server.Network;
using Server.Services.TownCryer;
#endregion

namespace Server
{
	public class CurrentExpansion
	{
		public static readonly Expansion Expansion = Config.GetEnum("Expansion.CurrentExpansion", Expansion.EJ);

		[CallPriority(Int32.MinValue)]
		public static void Configure()
		{
			Core.Expansion = Expansion;

			AccountGold.Enabled = Core.TOL;
			AccountGold.ConvertOnBank = true;
			AccountGold.ConvertOnTrade = false;
			VirtualCheck.UseEditGump = true;
            
			TownCryerSystem.Enabled = Core.TOL;

			// Hybrid: enable OPL (object property list) tooltips even on T2A as a QoL/readability
			// feature. This only affects client-side display; T2A item rules are unchanged
			// (items have no AOS properties, so tooltips just show name/durability/weight/etc.).
			ObjectPropertyList.Enabled = true;

			// The client only requests/draws tooltips when it sees the AOS feature flags.
			SupportedFeatures.Value |= FeatureFlags.AOS;
			CharacterList.AdditionalFlags |= CharacterListFlags.AOS;

            Mobile.InsuranceEnabled = Core.AOS && !Siege.SiegeShard;
			Mobile.VisibleDamageType = Core.AOS ? VisibleDamageType.Related : VisibleDamageType.None;
			Mobile.GuildClickMessage = !Core.AOS;
			Mobile.AsciiClickMessage = !Core.AOS;

			if (ObjectPropertyList.Enabled)
			{
				PacketHandlers.SingleClickProps = true; // single click for everything is overriden to check object property list
			}

			if (!Core.AOS)
			{
				return;
			}

			AOS.DisableStatInfluences();

			Mobile.ActionDelay = Core.TOL ? 500 : Core.AOS ? 1000 : 500;
			Mobile.AOSStatusHandler = AOS.GetStatus;
		}
	}
}