using System;
using System.Collections.Generic;
using System.Linq;
using Server.Items;
using Server.Mobiles;
using Server.Regions;
using Server.Spells;

namespace Server.Engines.Dueling
{
    /// <summary>
    /// One fenced 9x5 dueling ring with its own region, start marks, lobby exits and duel stone.
    /// Arena 1 sits on the z=20 plateau SW of the Minoc ridge; arenas 2-4 are on the empty grass strip
    /// in the north-east map quadrant (z=15, no spawns), 80 tiles apart so nothing can cross between them.
    /// </summary>
    public class DuelArena
    {
        public static readonly List<DuelArena> All = new List<DuelArena>();

        public static Map ArenaMap { get { return Map.Felucca; } }

        public const int AnnounceRange = 24;

        // Ring geometry, relative to the outer (fence) rectangle's top-left corner: outer 11x7, floor 9x5.
        public const int OuterWidth = 11;
        public const int OuterHeight = 7;

        public int Id { get; private set; }
        public int Z { get; private set; }

        /// <summary>Region bounds: fence ring included.</summary>
        public Rectangle2D Bounds { get; private set; }

        /// <summary>Walkable floor inside the fence.</summary>
        public Rectangle2D Floor { get; private set; }

        public Point3D MarkA { get; private set; }
        public Point3D MarkB { get; private set; }

        /// <summary>Where fighters are placed when a match ends (lobby strip south of the fence).</summary>
        public Point3D ExitA { get; private set; }
        public Point3D ExitB { get; private set; }

        public Point3D StoneLocation { get; private set; }
        public Point3D Center { get; private set; }

        public DuelRegion Region { get; private set; }
        public DuelMatch Match { get; set; }

        public bool Busy { get { return Match != null && Match.Phase != DuelPhase.Finished; } }

        private DuelArena(int id, int outerX, int outerY, int z)
        {
            Id = id;
            Z = z;

            Bounds = new Rectangle2D(outerX, outerY, OuterWidth, OuterHeight);
            Floor = new Rectangle2D(outerX + 1, outerY + 1, OuterWidth - 2, OuterHeight - 2);

            MarkA = new Point3D(outerX + 2, outerY + 3, z);
            MarkB = new Point3D(outerX + 8, outerY + 3, z);
            ExitA = new Point3D(outerX + 2, outerY + 8, z);
            ExitB = new Point3D(outerX + 8, outerY + 8, z);
            StoneLocation = new Point3D(outerX + 5, outerY + 8, z);
            Center = new Point3D(outerX + 5, outerY + 3, z);
        }

        /// <summary>Creates the arena definitions and registers their regions. Called once from DuelSystem.Initialize.</summary>
        public static void Setup()
        {
            if (All.Count > 0)
                return;

            All.Add(new DuelArena(1, 2597, 488, 20)); // Minoc ridge plateau
            All.Add(new DuelArena(2, 5175, 317, 15)); // NE grass strip
            All.Add(new DuelArena(3, 5255, 317, 15));
            All.Add(new DuelArena(4, 5335, 317, 15));

            foreach (DuelArena arena in All)
            {
                arena.Region = new DuelRegion(arena);
                arena.Region.Register();
            }
        }

        public static DuelArena Get(int id)
        {
            return All.FirstOrDefault(a => a.Id == id);
        }

        public static DuelArena FindFree()
        {
            return All.FirstOrDefault(a => !a.Busy);
        }

        /// <summary>The arena whose bounds contain the mobile's location, or null.</summary>
        public static DuelArena Find(Mobile m)
        {
            if (m == null || m.Map != ArenaMap)
                return null;

            return All.FirstOrDefault(a => a.Bounds.Contains(m.Location));
        }

        public bool Contains(Mobile m)
        {
            return m != null && m.Map == ArenaMap && Bounds.Contains(m.Location);
        }

        public Point3D MarkOf(DuelMatch match, Mobile m)
        {
            return m == match.A ? MarkA : MarkB;
        }

        public Point3D ExitOf(DuelMatch match, Mobile m)
        {
            return m == match.A ? ExitA : ExitB;
        }

        #region Construction

        // Short iron fence set: 0x0849 runs along world Y (west/east edges), 0x084B along world X (north/south edges), 0x084A is the post.
        private const int FenceAlongY = 0x0849;
        private const int FencePost = 0x084A;
        private const int FenceAlongX = 0x084B;

        public bool IsBuilt()
        {
            return World.Items.Values.Any(i => i is DuelArenaFence && !i.Deleted && ((DuelArenaFence)i).ArenaId == Id);
        }

        /// <summary>Builds every arena whose fence is missing. Returns the number of arenas built.</summary>
        public static int EnsureAllBuilt()
        {
            int built = 0;

            foreach (DuelArena arena in All)
            {
                if (arena.IsBuilt())
                    continue;

                int pieces = arena.Build();
                Console.WriteLine("[Duel] Arena {0} built: {1} fence pieces, stone at {2}.", arena.Id, pieces, arena.StoneLocation);
                built++;
            }

            return built;
        }

        public void Clear()
        {
            foreach (Item item in World.Items.Values.Where(i => (i is DuelArenaFence && ((DuelArenaFence)i).ArenaId == Id) || (i is DuelStone && ((DuelStone)i).ArenaId == Id)).ToList())
                item.Delete();
        }

        /// <summary>Places fence pieces on every ring tile at the arena's ground level (cliff tiles need none) plus the duel stone.</summary>
        public int Build()
        {
            Clear();

            Map map = ArenaMap;
            int count = 0;

            int x0 = Bounds.Start.X, y0 = Bounds.Start.Y;
            int x1 = Bounds.End.X - 1, y1 = Bounds.End.Y - 1;

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    bool onX = (y == y0 || y == y1);
                    bool onY = (x == x0 || x == x1);

                    if (!onX && !onY)
                        continue;

                    if (map.Tiles.GetLandTile(x, y).Z != Z)
                        continue; // cliff face above/below the floor already blocks this tile

                    int itemId = (onX && onY) ? FencePost : (onX ? FenceAlongX : FenceAlongY);

                    var fence = new DuelArenaFence(itemId, Id);
                    fence.MoveToWorld(new Point3D(x, y, Z), map);
                    count++;
                }
            }

            var stone = new DuelStone(Id);
            stone.MoveToWorld(StoneLocation, map);

            return count;
        }

        #endregion

        /// <summary>Moves every non-staff player standing in this arena (except the given fighters) out to the stone.</summary>
        public void EvictOthers(Mobile keepA, Mobile keepB)
        {
            if (Region == null)
                return;

            foreach (Mobile m in Region.GetPlayers())
            {
                if (m == keepA || m == keepB || m.IsStaff())
                    continue;

                m.MoveToWorld(StoneLocation, ArenaMap);
                m.SendMessage(DuelSystem.MessageHue, "[Duel] You have been moved out of the arena.");
            }
        }

        public string Describe()
        {
            return String.Format("Arena {0}: marks {1} / {2}, exits {3} / {4}, stone {5}", Id, MarkA, MarkB, ExitA, ExitB, StoneLocation);
        }
    }

    public class DuelRegion : BaseRegion
    {
        public const int RegionPriority = 60; // above TownRegion (50) so Minoc's guard zone never applies to arena 1

        public DuelArena Arena { get; private set; }

        public DuelRegion(DuelArena arena)
            : base(String.Format("Duel Arena {0}", arena.Id), DuelArena.ArenaMap, RegionPriority, arena.Bounds)
        {
            Arena = arena;
        }

        private DuelMatch Match { get { return Arena.Match; } }

        private bool RoundLive
        {
            get { return Match != null && (Match.Phase == DuelPhase.Countdown || Match.Phase == DuelPhase.Fighting); }
        }

        public override bool AllowHousing(Mobile from, Point3D p)
        {
            return false;
        }

        public override bool OnBeginSpellCast(Mobile m, ISpell s)
        {
            if (m.IsStaff())
                return base.OnBeginSpellCast(m, s);

            var match = Match;

            if (match != null && match.Rules.Magic && match.IsFighter(m))
            {
                if (match.Phase != DuelPhase.Fighting)
                {
                    m.SendMessage(DuelSystem.MessageHue, "[Duel] Wait for FIGHT! before casting.");
                    return false;
                }

                if (DuelRules.IsSpellBlocked(s))
                {
                    m.SendMessage(DuelSystem.MessageHue, "[Duel] That spell is not allowed in the arena.");
                    return false;
                }

                return base.OnBeginSpellCast(m, s);
            }

            m.SendMessage(DuelSystem.MessageHue, "[Duel] Spellcasting is not allowed in the arena.");
            return false;
        }

        public override bool AllowHarmful(Mobile from, IDamageable target)
        {
            if (from == null || from.IsStaff())
                return true;

            var match = Match;

            if (match == null || match.Phase != DuelPhase.Fighting)
                return false;

            var targetMobile = target as Mobile;

            return targetMobile != null && from != targetMobile && match.IsFighter(from) && match.IsFighter(targetMobile);
        }

        public override bool AllowBeneficial(Mobile from, Mobile target)
        {
            if (from == target || from.IsStaff())
                return true;

            var match = Match;

            if (match != null && match.Phase != DuelPhase.Finished && match.IsFighter(target))
                return false; // nobody may assist a duelist during a match

            return base.AllowBeneficial(from, target);
        }

        public override bool OnMoveInto(Mobile m, Direction d, Point3D newLocation, Point3D oldLocation)
        {
            if (RoundLive && !m.IsStaff() && !Match.IsFighter(m))
            {
                m.SendMessage(DuelSystem.MessageHue, "[Duel] A duel is in progress; you cannot enter the arena.");
                return false;
            }

            return base.OnMoveInto(m, d, newLocation, oldLocation);
        }

        public override bool OnDoubleClick(Mobile m, object o)
        {
            if (o is Corpse && ((Corpse)o).Owner != m && !m.IsStaff())
            {
                m.SendMessage(DuelSystem.MessageHue, "[Duel] You cannot loot corpses in the arena.");
                return false;
            }

            if (o is Bandage && !DuelSystem.AllowBandage(m, m))
                return false;

            return base.OnDoubleClick(m, o);
        }

        public override void OnDidHarmful(Mobile harmer, IDamageable harmed)
        {
            base.OnDidHarmful(harmer, harmed);

            var match = Match;

            if (match != null)
                match.NoteHarmful(harmer);
        }

        public override void OnDeath(Mobile m)
        {
            base.OnDeath(m);

            var match = Match;

            if (match != null)
                match.HandleDeath(m);
        }

        public override bool CheckTravel(Mobile traveller, Point3D p, TravelCheckType type)
        {
            // Nobody may recall/gate INTO the arena while a round is live; leaving is always allowed.
            if (RoundLive && !traveller.IsStaff() && !Match.IsFighter(traveller) && (type == TravelCheckType.RecallTo || type == TravelCheckType.GateTo))
                return false;

            return base.CheckTravel(traveller, p, type);
        }
    }

    public class DuelArenaFence : Item
    {
        [CommandProperty(AccessLevel.GameMaster)]
        public int ArenaId { get; set; }

        [Constructable]
        public DuelArenaFence(int itemId, int arenaId)
            : base(itemId)
        {
            ArenaId = arenaId;
            Movable = false;
            Name = "arena fence";
        }

        public DuelArenaFence(Serial serial)
            : base(serial)
        {
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(1); // version
            writer.Write(ArenaId);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            int version = reader.ReadInt();
            ArenaId = version >= 1 ? reader.ReadInt() : 1;
        }
    }

    public class DuelStone : Item
    {
        [CommandProperty(AccessLevel.GameMaster)]
        public int ArenaId { get; set; }

        [Constructable]
        public DuelStone(int arenaId)
            : base(0xEDD)
        {
            ArenaId = arenaId;
            Movable = false;
            Hue = 0x4E9;
            Name = String.Format("duel stone (arena {0})", arenaId);
        }

        public DuelStone(Serial serial)
            : base(serial)
        {
        }

        public override void OnDoubleClick(Mobile from)
        {
            if (!from.InRange(GetWorldLocation(), 8))
            {
                from.SendMessage(DuelSystem.MessageHue, "[Duel] You are too far away from the duel stone.");
                return;
            }

            DuelCommands.SendUsage(from);

            DuelArena arena = DuelArena.Get(ArenaId);

            if (arena != null)
                from.SendMessage(DuelSystem.MessageHue, DuelCommands.ArenaStatusLine(arena));
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(1); // version
            writer.Write(ArenaId);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            int version = reader.ReadInt();
            ArenaId = version >= 1 ? reader.ReadInt() : 1;

            if (version < 1)
                Name = "duel stone (arena 1)";
        }
    }
}
