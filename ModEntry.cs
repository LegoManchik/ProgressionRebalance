using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Quests;
using xTile;
using xTile.Layers;
using xTile.Tiles;

namespace ModpackProgressionRebalance
{
    public class ModEntry : Mod
    {
        public static ModEntry Instance { get; private set; } = null!;
        public ModConfig Config { get; private set; } = null!;

        // Flag and ID constants
        public const string FlagFiasaArrived = "LegoManchik.ProgressionRebalance.FIASA_Arrived";
        public const string FlagWillyMailSent = "LegoManchik.ProgressionRebalance.WillyLetterSent";
        public const string MailWillyFiasa = "LegoManchik.ProgressionRebalance.WillyFIASA";
        public const string QuestAquariumId = "LegoManchik.ProgressionRebalance.AquariumQuest";
        public const string FlagAquariumQuestReceived = "LegoManchik.ProgressionRebalance.AquariumQuestReceived";
        public const string FlagAquariumQuestCompleted = "LegoManchik.ProgressionRebalance.AquariumQuestCompleted";
        public const string FlagAquariumVisited = "LegoManchik.ProgressionRebalance.AquariumVisited";

        // Mod installed flags
        private bool isTrolleyInstalled;
        private bool isWalkToDesertInstalled;
        private bool isCentralStationInstalled;
        private bool isAquariumInstalled;

        // Rollback state (fallback safety only)
        private bool isRollingBack;
        private string? pendingDialogueMessage;
        private string? lastSafeLocation;
        private Vector2 lastSafeTile;
        private int lastSafeFacing = 2;

        // Daily interaction tracking
        private bool hasWillySpokenToday;
        private bool hasInkeSpokenToday;

        // Hook for Central Station ticket action
        private Func<GameLocation, string[], Farmer, Point, bool>? underlyingCentralStationHandler;
        private bool isCentralStationHooked;

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            Config = helper.ReadConfig<ModConfig>();

            // Apply Harmony patches
            var harmony = new Harmony(ModManifest.UniqueID);
            PatchBusLocations(harmony);
            PatchCentralStation(harmony);

            // Register custom tile actions for blocked paths
            GameLocation.RegisterTileAction("ProgressionRebalance_FiasaBlocked", HandleFiasaBlockedTileAction);
            GameLocation.RegisterTileAction("ProgressionRebalance_WalkDesertBlocked", HandleWalkDesertBlockedTileAction);
            GameLocation.RegisterTileAction("ProgressionRebalance_TrolleyDesertBlocked", HandleTrolleyDesertBlockedTileAction);

            // Register touch actions
            GameLocation.RegisterTouchAction("ProgressionRebalance_FiasaBlocked", (loc, args, who, tile) =>
            {
                Game1.drawObjectDialogue(Helper.Translation.Get("gate.fiasa_closed"));
            });
            GameLocation.RegisterTouchAction("ProgressionRebalance_WalkDesertBlocked", (loc, args, who, tile) =>
            {
                Game1.drawObjectDialogue(Helper.Translation.Get("gate.walk_desert_not_repaired"));
            });
            GameLocation.RegisterTouchAction("ProgressionRebalance_TrolleyDesertBlocked", (loc, args, who, tile) =>
            {
                Game1.drawObjectDialogue(Helper.Translation.Get("gate.trolley_desert_closed"));
            });

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.UpdateTicking += OnUpdateTicking;
            helper.Events.Player.Warped += OnWarped;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.Display.MenuChanged += OnMenuChanged;
            helper.Events.Content.AssetRequested += OnAssetRequested;
            helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
            helper.Events.Multiplayer.PeerContextReceived += OnPeerContextReceived;
        }

        private void PatchBusLocations(Harmony harmony)
        {
            try
            {
                Type? busLocType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    busLocType = asm.GetType("BusLocations.ModEntry");
                    if (busLocType != null)
                        break;
                }

                if (busLocType == null)
                    return;

                var prefix = typeof(ModEntry).GetMethod(nameof(BusLocations_IsPlayerAtTicketMachine_Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix == null)
                    return;

                var methodVec = busLocType.GetMethod("IsPlayerAtTicketMachine", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Vector2) }, null);
                if (methodVec != null)
                {
                    harmony.Patch(methodVec, prefix: new HarmonyMethod(prefix));
                    Monitor.Log("Harmony patched BusLocations.IsPlayerAtTicketMachine(Vector2) to unify terminals.", LogLevel.Debug);
                }

                var methodNoArgs = busLocType.GetMethod("IsPlayerAtTicketMachine", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes);
                if (methodNoArgs != null)
                {
                    harmony.Patch(methodNoArgs, prefix: new HarmonyMethod(prefix));
                    Monitor.Log("Harmony patched BusLocations.IsPlayerAtTicketMachine() to unify terminals.", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not patch BusLocations with Harmony: {ex.Message}", LogLevel.Debug);
            }
        }

        private static bool BusLocations_IsPlayerAtTicketMachine_Prefix(ref bool __result)
        {
            __result = false;
            return false; // Skip original method completely
        }

        private void PatchCentralStation(Harmony harmony)
        {
            try
            {
                Type? contentManagerType = null;
                Type? stopManagerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (contentManagerType == null)
                        contentManagerType = asm.GetType("CentralStation.Internal.ContentManager");
                    if (stopManagerType == null)
                        stopManagerType = asm.GetType("CentralStation.Internal.StopManager");
                    if (contentManagerType != null && stopManagerType != null)
                        break;
                }

                if (contentManagerType != null)
                {
                    // 1. Force HomeBusStopHasTicketMachine to return true so Central Station ticket machine appearance is placed even before repair
                    var homeBusStopMethod = contentManagerType.GetMethod("HomeBusStopHasTicketMachine", BindingFlags.Instance | BindingFlags.NonPublic);
                    var homeBusStopPrefix = typeof(ModEntry).GetMethod(nameof(CentralStation_HomeBusStopHasTicketMachine_Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                    if (homeBusStopMethod != null && homeBusStopPrefix != null)
                    {
                        harmony.Patch(homeBusStopMethod, prefix: new HarmonyMethod(homeBusStopPrefix));
                        Monitor.Log("Harmony patched CentralStation.HomeBusStopHasTicketMachine() to always show machine.", LogLevel.Debug);
                    }

                    // 2. Force GetBusStopTicketMachineIndexOffset to return 0 so CS machine is placed at (17, 11) rather than (18, 11)
                    var offsetMethod = contentManagerType.GetMethod("GetBusStopTicketMachineIndexOffset", BindingFlags.Instance | BindingFlags.NonPublic);
                    var offsetPrefix = typeof(ModEntry).GetMethod(nameof(CentralStation_GetBusStopTicketMachineIndexOffset_Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                    if (offsetMethod != null && offsetPrefix != null)
                    {
                        harmony.Patch(offsetMethod, prefix: new HarmonyMethod(offsetPrefix));
                        Monitor.Log("Harmony patched CentralStation.GetBusStopTicketMachineIndexOffset() to 0.", LogLevel.Debug);
                    }

                    // 3. Strip any [Bus Location], [Bus Locations mod], [Train Station mod] suffixes from labels
                    var labelMethod = contentManagerType.GetMethod("GetStopLabel", BindingFlags.Instance | BindingFlags.Public);
                    var labelPostfix = typeof(ModEntry).GetMethod(nameof(CentralStation_GetStopLabel_Postfix), BindingFlags.Static | BindingFlags.NonPublic);
                    if (labelMethod != null && labelPostfix != null)
                    {
                        harmony.Patch(labelMethod, postfix: new HarmonyMethod(labelPostfix));
                        Monitor.Log("Harmony patched CentralStation.GetStopLabel() to clean destination labels.", LogLevel.Debug);
                    }
                }

                if (stopManagerType != null)
                {
                    // 4. Hide Aquarium stop in Central Station until quest is received from Inke
                    var shouldEnableMethod = stopManagerType.GetMethod("ShouldEnableStop", BindingFlags.Instance | BindingFlags.NonPublic);
                    var shouldEnablePostfix = typeof(ModEntry).GetMethod(nameof(CentralStation_ShouldEnableStop_Postfix), BindingFlags.Static | BindingFlags.NonPublic);
                    if (shouldEnableMethod != null && shouldEnablePostfix != null)
                    {
                        harmony.Patch(shouldEnableMethod, postfix: new HarmonyMethod(shouldEnablePostfix));
                        Monitor.Log("Harmony patched CentralStation.ShouldEnableStop() to gate Aquarium stop by Inke quest.", LogLevel.Debug);
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not patch Central Station with Harmony: {ex.Message}", LogLevel.Debug);
            }
        }

        private static bool CentralStation_HomeBusStopHasTicketMachine_Prefix(ref bool __result)
        {
            __result = true;
            return false;
        }

        private static bool CentralStation_GetBusStopTicketMachineIndexOffset_Prefix(ref int __result)
        {
            __result = 0;
            return false;
        }

        private static void CentralStation_GetStopLabel_Postfix(ref string __result)
        {
            if (!string.IsNullOrEmpty(__result))
            {
                __result = Regex.Replace(__result, @"\s*\[(Bus Locations?|Train Station)( mod)?\]\s*", "", RegexOptions.IgnoreCase).Trim();
            }
        }

        private static void CentralStation_ShouldEnableStop_Postfix(string id, string stopLocation, ref bool __result)
        {
            if (__result)
            {
                if (id.Contains("Aquarium", StringComparison.OrdinalIgnoreCase) ||
                    stopLocation.Contains("Aquarium", StringComparison.OrdinalIgnoreCase) ||
                    stopLocation.Contains("ExteriorMuseum", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ModEntry.Instance.HasReceivedAquariumQuest())
                    {
                        __result = false;
                    }
                }
            }
        }

        private bool HandleFiasaBlockedTileAction(GameLocation location, string[] args, Farmer who, Point tile)
        {
            Game1.drawObjectDialogue(Helper.Translation.Get("gate.fiasa_closed"));
            return true;
        }

        private bool HandleWalkDesertBlockedTileAction(GameLocation location, string[] args, Farmer who, Point tile)
        {
            Game1.drawObjectDialogue(Helper.Translation.Get("gate.walk_desert_not_repaired"));
            return true;
        }

        private bool HandleTrolleyDesertBlockedTileAction(GameLocation location, string[] args, Farmer who, Point tile)
        {
            Game1.drawObjectDialogue(Helper.Translation.Get("gate.trolley_desert_closed"));
            return true;
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            isTrolleyInstalled = Helper.ModRegistry.IsLoaded("cajcbell.TE");
            isWalkToDesertInstalled = Helper.ModRegistry.IsLoaded("Tarniyar.CP.WalkToDesert");
            isCentralStationInstalled = Helper.ModRegistry.IsLoaded("Pathoschild.CentralStation");
            isAquariumInstalled = Helper.ModRegistry.IsLoaded("Cherry.StardewAquarium") || Helper.ModRegistry.IsLoaded("Gervig91.StardewAquariumCP");

            if (!isTrolleyInstalled)
                Monitor.Log("Trolley Expansion mod not found. Corresponding progression gates will be disabled.", LogLevel.Info);
            if (!isWalkToDesertInstalled)
                Monitor.Log("Walk to the Desert mod not found. Corresponding progression gate will be disabled.", LogLevel.Info);
            if (!isCentralStationInstalled)
                Monitor.Log("Central Station mod not found. Central Station gate will be disabled.", LogLevel.Info);
            if (!isAquariumInstalled)
                Monitor.Log("Stardew Aquarium mod not found. Aquarium quest will be disabled.", LogLevel.Info);

            RegisterGenericModConfigMenu();
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            hasWillySpokenToday = false;
            hasInkeSpokenToday = false;
            lastSafeLocation = null;
            lastSafeTile = Vector2.Zero;
            isRollingBack = false;
            pendingDialogueMessage = null;

            HookCentralStationTileAction();
            CheckFiasaArrival(isDayStart: false);
            ApplyLiveLocationBlocksToAll();
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            hasWillySpokenToday = false;
            hasInkeSpokenToday = false;

            HookCentralStationTileAction();
            CheckFiasaArrival(isDayStart: true);
            ApplyLiveLocationBlocksToAll();

            if (Game1.year == 1 && Game1.season == Season.Summer && Game1.dayOfMonth == 1)
            {
                Helper.GameContent.InvalidateCache("Maps/Tunnel");
            }
        }

        private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.currentLocation == null)
                return;

            if (!isCentralStationHooked && isCentralStationInstalled)
            {
                HookCentralStationTileAction();
            }

            string locName = Game1.currentLocation.NameOrUniqueName;
            if (!IsGatedLocation(locName))
            {
                lastSafeLocation = locName;
                lastSafeTile = Game1.player.Tile;
                lastSafeFacing = Game1.player.FacingDirection;
            }
        }

        // --- Central Station TileAction Hook ---

        private void HookCentralStationTileAction()
        {
            try
            {
                var dict = GetRegisteredTileActions();
                if (dict != null && dict.TryGetValue("CentralStation", out var originalHandler))
                {
                    if (originalHandler != HandleCentralStationTickets)
                    {
                        underlyingCentralStationHandler = originalHandler;
                        dict["CentralStation"] = HandleCentralStationTickets;
                        isCentralStationHooked = true;
                        Monitor.Log("Hooked Central Station ticket action to unify terminals.", LogLevel.Debug);
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not hook Central Station ticket action: {ex}", LogLevel.Warn);
            }
        }

        private Dictionary<string, Func<GameLocation, string[], Farmer, Point, bool>>? GetRegisteredTileActions()
        {
            FieldInfo? field = typeof(GameLocation).GetField("registeredTileActions", BindingFlags.NonPublic | BindingFlags.Static);
            return field?.GetValue(null) as Dictionary<string, Func<GameLocation, string[], Farmer, Point, bool>>;
        }

        private bool HandleCentralStationTickets(GameLocation location, string[] args, Farmer who, Point tile)
        {
            if (Config.EnableCentralStationGate && !IsBusRepaired())
            {
                Game1.drawObjectDialogue(Helper.Translation.Get("gate.terminal_out_of_service"));
                return true;
            }
            return underlyingCentralStationHandler?.Invoke(location, args, who, tile) ?? false;
        }

        private void OnWarped(object? sender, WarpedEventArgs e)
        {
            if (!e.IsLocalPlayer || e.NewLocation == null)
                return;

            try
            {
                ApplyLiveLocationBlocks(e.NewLocation);

                if (isRollingBack)
                {
                    isRollingBack = false;
                    if (pendingDialogueMessage != null)
                    {
                        string msg = pendingDialogueMessage;
                        pendingDialogueMessage = null;
                        Game1.drawObjectDialogue(msg);
                    }
                    return;
                }

                string newLoc = e.NewLocation.NameOrUniqueName;
                string oldLoc = e.OldLocation?.NameOrUniqueName ?? "";

                // 1. Fallback safety: FIASA closed until Summer 1 Year 1
                if (Config.EnableFIASAGate && isTrolleyInstalled && IsFIASALocation(newLoc))
                {
                    if (!IsFIASAArrived())
                    {
                        Monitor.Log($"Player bypassed block into FIASA location '{newLoc}'. Rolling back.", LogLevel.Debug);
                        Rollback(e.OldLocation, Helper.Translation.Get("gate.fiasa_closed"));
                        return;
                    }
                }



                // 4. Fallback safety: Central Station closed until bus repair
                if (Config.EnableCentralStationGate && isCentralStationInstalled && IsCentralStationLocation(newLoc))
                {
                    if (!IsBusRepaired())
                    {
                        Monitor.Log("Player entered Central Station before bus repair. Rolling back.", LogLevel.Debug);
                        Rollback(e.OldLocation, Helper.Translation.Get("gate.terminal_out_of_service"));
                        return;
                    }
                }

                // Aquarium entry & quest completion
                if (IsAquariumLocation(newLoc))
                {
                    OnEnteredAquarium();
                }
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Error handling location warp in progression rebalance: {ex}", LogLevel.Warn);
            }
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.currentLocation == null)
                return;

            if (!e.Button.IsActionButton())
                return;

            try
            {
                string locName = Game1.currentLocation.Name;
                Vector2 cursor = Game1.currentCursorTile;
                Vector2 grab = Game1.player.GetGrabTile();

                // 1. Bus Terminal at BusStop (Unified terminal handling)
                if (locName == "BusStop" && IsInteractingWithBusTerminal(cursor, grab))
                {
                    Helper.Input.Suppress(e.Button);

                    if (Config.EnableCentralStationGate && !IsBusRepaired())
                    {
                        Game1.drawObjectDialogue(Helper.Translation.Get("gate.terminal_out_of_service"));
                        return;
                    }

                    // Bus is repaired!
                    if (isCentralStationInstalled)
                    {
                        var actions = GetRegisteredTileActions();
                        if (actions != null && actions.TryGetValue("CentralStation", out var csHandler))
                        {
                            csHandler(Game1.currentLocation, new[] { "CentralStation", "Bus" }, Game1.player, new Point(17, 11));
                            return;
                        }
                    }

                    // Fallback to vanilla action
                    Game1.currentLocation.performAction("BusTicket", Game1.player, new xTile.Dimensions.Location(17, 11));
                    return;
                }



                // 4. Blocked FIASA tile in Forest or Beach before Summer 1 Year 1
                if (Config.EnableFIASAGate && isTrolleyInstalled && !IsFIASAArrived())
                {
                    if (locName == "Forest")
                    {
                        if ((cursor.X >= 4 && cursor.X <= 6 && cursor.Y >= 67 && cursor.Y <= 69) ||
                            (grab.X >= 4 && grab.X <= 6 && grab.Y >= 67 && grab.Y <= 69))
                        {
                            Game1.drawObjectDialogue(Helper.Translation.Get("gate.fiasa_closed"));
                            Helper.Input.Suppress(e.Button);
                            return;
                        }
                    }
                    else if (locName == "Beach" || locName == "Beach-NightMarket")
                    {
                        if ((cursor.X >= 9 && cursor.X <= 12 && cursor.Y >= 0 && cursor.Y <= 3) ||
                            (grab.X >= 9 && grab.X <= 12 && grab.Y >= 0 && grab.Y <= 3))
                        {
                            Game1.drawObjectDialogue(Helper.Translation.Get("gate.fiasa_closed"));
                            Helper.Input.Suppress(e.Button);
                            return;
                        }
                    }
                }



                // 6. Willy on arrival day
                if (IsArrivalDay() && !hasWillySpokenToday)
                {
                    NPC? willy = Game1.currentLocation.getCharacterFromName("Willy");
                    if (willy != null && IsPlayerNearNpc(willy))
                    {
                        hasWillySpokenToday = true;
                        willy.setNewDialogue(Helper.Translation.Get("willy.fiasa_dialogue"));
                        Game1.drawDialogue(willy);
                        return;
                    }
                }

                // 7. Inke grants the Aquarium quest upon conversation after bus repair + FIASA arrival
                if (IsBusRepaired() && IsFIASAArrived())
                {
                    NPC? inke = Game1.currentLocation.getCharacterFromName("CC_Inke");
                    if (inke != null && IsPlayerNearNpc(inke))
                    {
                        if (!HasReceivedAquariumQuest())
                        {
                            Helper.Input.Suppress(e.Button);
                            GrantAquariumQuestFromInke(inke);
                            return;
                        }
                        else if (!HasVisitedAquarium() && !hasInkeSpokenToday)
                        {
                            hasInkeSpokenToday = true;
                            inke.setNewDialogue(Helper.Translation.Get("inke.aquarium_dialogue"));
                            Game1.drawDialogue(inke);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Error handling button action in progression rebalance: {ex}", LogLevel.Warn);
            }
        }

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (e.NewMenu is DialogueBox db)
            {
                if (db.characterDialogue?.speaker?.Name == "CC_Inke")
                {
                    if (IsBusRepaired() && IsFIASAArrived() && !HasReceivedAquariumQuest())
                    {
                        GrantAquariumQuestFromInke(db.characterDialogue.speaker);
                    }
                }
            }
        }

        private void GrantAquariumQuestFromInke(NPC inke)
        {
            hasInkeSpokenToday = true;
            Game1.player.mailReceived.Add(FlagAquariumQuestReceived);
            Game1.MasterPlayer.mailReceived.Add(FlagAquariumQuestReceived);

            Game1.player.addQuest(QuestAquariumId);
            Game1.playSound("questcomplete");

            string title = Helper.Translation.Get("aquarium_quest.title");
            Game1.addHUDMessage(new HUDMessage(title, HUDMessage.achievement_type));

            Helper.Multiplayer.SendMessage(
                new AquariumQuestStateMessage { IsActive = true, IsCompleted = false, Visited = false },
                nameof(AquariumQuestStateMessage),
                modIDs: new[] { ModManifest.UniqueID }
            );

            // Invalidate stops cache so Aquarium stop now appears in Central Station's menu
            Helper.GameContent.InvalidateCache("Mods/Pathoschild.CentralStation/Stops");

            inke.setNewDialogue(Helper.Translation.Get("inke.aquarium_dialogue"));
            Game1.drawDialogue(inke);

            Monitor.Log("Granted Stardew Aquarium quest via Inke (captain of FIASA).", LogLevel.Info);
        }

        private bool IsInteractingWithBusTerminal(Vector2 cursor, Vector2 grab)
        {
            bool cursorMatch = (int)cursor.X == 17 && (int)cursor.Y >= 10 && (int)cursor.Y <= 12 && Utility.tileWithinRadiusOfPlayer((int)cursor.X, (int)cursor.Y, 2, Game1.player);
            bool grabMatch = (int)grab.X == 17 && (int)grab.Y >= 10 && (int)grab.Y <= 12;
            return cursorMatch || grabMatch;
        }

        private bool IsTileMatching(Vector2 tile, int x, int y)
        {
            return (int)tile.X == x && (int)tile.Y == y;
        }

        private bool IsPlayerNearNpc(NPC npc)
        {
            Vector2 playerTile = Game1.player.Tile;
            Vector2 npcTile = npc.Tile;
            return Math.Abs(playerTile.X - npcTile.X) <= 2 && Math.Abs(playerTile.Y - npcTile.Y) <= 2;
        }

        private void Rollback(GameLocation? oldLocation, string message)
        {
            isRollingBack = true;
            pendingDialogueMessage = message;

            string targetMap = oldLocation?.NameOrUniqueName ?? lastSafeLocation ?? "Farm";
            Point safeTile;

            if (oldLocation != null && lastSafeLocation == oldLocation.NameOrUniqueName && lastSafeTile != Vector2.Zero)
            {
                safeTile = new Point((int)lastSafeTile.X, (int)lastSafeTile.Y);
            }
            else
            {
                safeTile = GetDefaultSafeTile(targetMap);
            }

            Game1.warpFarmer(targetMap, safeTile.X, safeTile.Y, lastSafeFacing);
        }

        private Point GetDefaultSafeTile(string mapName)
        {
            return mapName switch
            {
                "Forest" => new Point(7, 68),
                "Beach" or "Beach-NightMarket" => new Point(10, 3),
                "Tunnel" => new Point(2, 9),
                "Backwoods" => new Point(30, 24),
                "Desert" or "Desert-Festival" => new Point(18, 28),
                "BusStop" => new Point(12, 11),
                "Trolley_Pond" => new Point(27, 17),
                _ => new Point(1, 1)
            };
        }

        // --- Gate Location Checks ---

        private bool IsGatedLocation(string locName)
        {
            return (Config.EnableFIASAGate && IsFIASALocation(locName))
                || (Config.EnableTrolleyDesertGate && IsTrolleyDesertLocation(locName))
                || (Config.EnableWalkToDesertGate && isWalkToDesertInstalled && IsBeforeFirstSummer() && IsWalkToDesertLocation(locName))
                || (Config.EnableCentralStationGate && IsCentralStationLocation(locName));
        }

        public bool IsBeforeFirstSummer()
        {
            return Game1.year == 1 && Game1.season == Season.Spring;
        }

        public bool IsFIASALocation(string locName)
        {
            return locName switch
            {
                "Trolley_Pond" => true,
                "Cove_exit" => true,
                "Koda_Cove" => true,
                "Cove_Kaho" => true,
                "Koda_Sub" => true,
                "Koda_Sub_rain" => true,
                "Koda_room" => true,
                "Koda_patio" => true,
                "Kaho_room" => true,
                "Kaho_patio" => true,
                "Taro_Entrance" => true,
                "Taro_Meadow" => true,
                "Taro_Tent" => true,
                "Taro_Train" => true,
                "Sub_enter" => true,
                "Sub_horizon" => true,
                "Turtle_Cove" => true,
                "Turtle_Cove_party" => true,
                _ => false
            };
        }

        public bool IsTrolleyDesertLocation(string locName)
        {
            return locName switch
            {
                "Canyon_south" => true,
                "Canyon_final" => true,
                "Canyon_flood" => true,
                "Cave_north" => true,
                "Cave_dwarf" => true,
                "north_flood" => true,
                _ => false
            };
        }

        public bool IsWalkToDesertLocation(string locName)
        {
            return locName.StartsWith("Tarniyar.CP.WalkToDesert_")
                || locName.StartsWith("Custom_WTDR_")
                || locName == "Meadow"
                || locName == "ForestRoad"
                || locName == "Interchange"
                || locName == "GhostTown"
                || locName == "WindingRoad";
        }

        public bool IsDesertLocation(string locName)
        {
            return locName == "Desert" || locName == "Desert-Festival";
        }

        public bool IsCentralStationLocation(string locName)
        {
            return locName == "Pathoschild.CentralStation_CentralStation";
        }

        public bool IsAquariumLocation(string locName)
        {
            return locName.Contains("Aquarium")
                || locName.Contains("ExteriorMuseum")
                || locName.Contains("FishMuseum")
                || locName.Contains("BigRoom");
        }

        // --- Progression Conditions ---

        public bool IsBusRepaired()
        {
            try
            {
                if (GameStateQuery.CheckConditions("LOCATION_ACCESSIBLE Desert"))
                    return true;
            }
            catch { }

            return Game1.MasterPlayer.hasOrWillReceiveMail("ccVault")
                || Game1.MasterPlayer.hasOrWillReceiveMail("JojaVault")
                || Game1.netWorldState.Value.canDriveYourselfToday.Value;
        }

        public bool IsFIASAArrived()
        {
            if (Game1.player.mailReceived.Contains(FlagFiasaArrived) || Game1.MasterPlayer.mailReceived.Contains(FlagFiasaArrived))
                return true;

            int targetSeasonIndex = GetSeasonIndex(Config.FIASAArrivalSeason);
            int currentSeasonIndex = GetSeasonIndex(Game1.currentSeason);

            if (Game1.year > Config.FIASAArrivalYear)
                return true;

            if (Game1.year == Config.FIASAArrivalYear)
            {
                if (currentSeasonIndex > targetSeasonIndex)
                    return true;
                if (currentSeasonIndex == targetSeasonIndex && Game1.dayOfMonth >= Config.FIASAArrivalDay)
                    return true;
            }

            return false;
        }

        private bool IsArrivalDay()
        {
            int targetSeasonIndex = GetSeasonIndex(Config.FIASAArrivalSeason);
            int currentSeasonIndex = GetSeasonIndex(Game1.currentSeason);

            return Game1.year == Config.FIASAArrivalYear
                && currentSeasonIndex == targetSeasonIndex
                && Game1.dayOfMonth == Config.FIASAArrivalDay;
        }

        private int GetSeasonIndex(string season)
        {
            return season.ToLowerInvariant() switch
            {
                "spring" => 0,
                "summer" => 1,
                "fall" => 2,
                "winter" => 3,
                _ => 1
            };
        }

        public bool HasVisitedAquarium()
        {
            return Game1.player.mailReceived.Contains(FlagAquariumVisited)
                || Game1.MasterPlayer.mailReceived.Contains(FlagAquariumVisited);
        }

        public bool HasReceivedAquariumQuest()
        {
            return Game1.player.mailReceived.Contains(FlagAquariumQuestReceived)
                || Game1.MasterPlayer.mailReceived.Contains(FlagAquariumQuestReceived);
        }

        public bool IsAquariumQuestCompleted()
        {
            return Game1.player.mailReceived.Contains(FlagAquariumQuestCompleted)
                || Game1.MasterPlayer.mailReceived.Contains(FlagAquariumQuestCompleted);
        }

        private void CheckFiasaArrival(bool isDayStart)
        {
            if (IsFIASAArrived())
            {
                bool firstTime = !Game1.player.mailReceived.Contains(FlagFiasaArrived);
                if (firstTime)
                {
                    Game1.player.mailReceived.Add(FlagFiasaArrived);
                    Game1.MasterPlayer.mailReceived.Add(FlagFiasaArrived);

                    Helper.GameContent.InvalidateCache("Maps/Forest");
                    Helper.GameContent.InvalidateCache("Maps/Beach");
                    Helper.GameContent.InvalidateCache("Maps/Beach-NightMarket");

                    Game1.getLocationFromName("Forest")?.reloadMap();
                    Game1.getLocationFromName("Beach")?.reloadMap();
                    Game1.getLocationFromName("Beach-NightMarket")?.reloadMap();

                    Helper.Multiplayer.SendMessage(
                        new FiasaArrivedMessage { Arrived = true },
                        nameof(FiasaArrivedMessage),
                        modIDs: new[] { ModManifest.UniqueID }
                    );

                    Monitor.Log("FIASA has arrived! Path unblocked and flag synchronized.", LogLevel.Info);
                }

                if (!Game1.player.mailReceived.Contains(FlagWillyMailSent))
                {
                    Game1.player.mailReceived.Add(FlagWillyMailSent);
                    Game1.player.mailbox.Add(MailWillyFiasa);
                    Monitor.Log("Willy's letter regarding strange sights in fog added to mailbox.", LogLevel.Debug);
                }
            }
        }

        private void OnEnteredAquarium()
        {
            bool firstVisit = !HasVisitedAquarium();
            Game1.player.mailReceived.Add(FlagAquariumVisited);
            Game1.MasterPlayer.mailReceived.Add(FlagAquariumVisited);

            Quest? activeQuest = Game1.player.questLog.FirstOrDefault(q => q.id.Value == QuestAquariumId);
            if (activeQuest != null && !activeQuest.completed.Value)
            {
                activeQuest.questComplete();
                Game1.player.Money += Config.AquariumQuestRewardGold;
                Game1.playSound("questcomplete");

                string hudMsg = Helper.Translation.Get("aquarium_quest.completed_hud", new { reward = Config.AquariumQuestRewardGold });
                Game1.addHUDMessage(new HUDMessage(hudMsg, HUDMessage.achievement_type));

                Game1.player.mailReceived.Add(FlagAquariumQuestCompleted);
                Game1.MasterPlayer.mailReceived.Add(FlagAquariumQuestCompleted);

                Helper.Multiplayer.SendMessage(
                    new AquariumQuestStateMessage { IsActive = false, IsCompleted = true, Visited = true },
                    nameof(AquariumQuestStateMessage),
                    modIDs: new[] { ModManifest.UniqueID }
                );

                Monitor.Log($"Aquarium quest completed. Awarded {Config.AquariumQuestRewardGold} gold.", LogLevel.Info);
            }
            else if (firstVisit)
            {
                Helper.Multiplayer.SendMessage(
                    new AquariumQuestStateMessage { IsActive = false, IsCompleted = IsAquariumQuestCompleted(), Visited = true },
                    nameof(AquariumQuestStateMessage),
                    modIDs: new[] { ModManifest.UniqueID }
                );
            }
        }

        // --- Map Blocker & Terminal Unification Operations ---

        private void ApplyLiveLocationBlocksToAll()
        {
            foreach (GameLocation location in Game1.locations)
            {
                ApplyLiveLocationBlocks(location);
            }
        }

        private void ApplyLiveLocationBlocks(GameLocation? loc)
        {
            if (loc?.Map == null)
                return;

            string locName = loc.Name;

            // 1. BusStop unification (Central Station ticket machine on 17, clear 18)
            if (locName == "BusStop")
            {
                ApplyBusStopUnification(loc.Map);
            }

            // 2. FIASA (Forest, Beach)
            if (Config.EnableFIASAGate && isTrolleyInstalled && !IsFIASAArrived())
            {
                if (locName == "Forest")
                {
                    ApplyForestFiasaBlock(loc.Map);
                    RemoveLiveWarps(loc, "Trolley_Pond");
                }
                else if (locName == "Beach" || locName == "Beach-NightMarket")
                {
                    ApplyBeachFiasaBlock(loc.Map);
                    RemoveLiveWarps(loc, "Cove_exit");
                }
            }

            // 3. Walk to Desert (Tunnel & Backwoods & Forest_Road)
            if (isWalkToDesertInstalled)
            {
                if (locName == "Tunnel" && Config.EnableWalkToDesertGate && IsBeforeFirstSummer())
                {
                    ApplyTunnelWalkDesertBlock(loc.Map);
                    RemoveLiveWarps(loc, "Forest_Road");
                    RemoveLiveWarps(loc, "WalkToDesert");
                }
                else if (locName == "Backwoods" && Config.RemoveHorseWarp)
                {
                    ApplyBackwoodsHorseWarpRemoval(loc.Map);
                    RemoveLiveWarps(loc, "Forest_Road");
                    RemoveLiveWarps(loc, "WalkToDesert");
                }
                else if (locName.Contains("Forest_Road", StringComparison.OrdinalIgnoreCase) && Config.RemoveHorseWarp)
                {
                    ApplyForestRoadHorseWarpRemoval(loc.Map);
                    RemoveLiveWarps(loc, "Backwoods");
                }
            }

            // 4. Railroad Unification (remove Grampleton terminal at 30, 39..40)
            if (locName == "Railroad" && Config.RemoveRailroadGrampletonTerminal)
            {
                ApplyRailroadUnification(loc.Map);
                RemoveLiveWarps(loc, "Custom_GrampletonFields");
                RemoveLiveWarps(loc, "GrampletonFields");
            }

            // 5. Grampleton Fields Central Station Terminal
            if ((locName == "Custom_GrampletonFields" || locName == "GrampletonFields") && Config.EnableGrampletonFieldsCentralStation)
            {
                ApplyGrampletonFieldsCentralStationTerminal(loc.Map);
            }


        }

        private void ApplyBusStopUnification(Map map)
        {
            var buildings = map.GetLayer("Buildings");
            var front = map.GetLayer("Front");

            // 1. Remove duplicate Central Station ticket machine at tile (18, 10..11)
            if (buildings != null && 18 < buildings.LayerWidth && 11 < buildings.LayerHeight)
            {
                buildings.Tiles[18, 11] = null;
            }
            if (front != null && 18 < front.LayerWidth && 10 < front.LayerHeight)
            {
                front.Tiles[18, 10] = null;
            }

            // 2. Ensure tile (17, 10..11) has Central Station's ticket machine appearance
            try
            {
                Map ticketMachineMap = Helper.GameContent.Load<Map>("Mods/Pathoschild.CentralStation/TicketMachine");
                var patchHelper = Helper.GameContent.GetPatchHelper(map, "Maps/BusStop");
                patchHelper.AsMap().PatchMap(ticketMachineMap, null, new Rectangle(17, 10, 1, 2));
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not patch Central Station ticket machine visual onto (17, 10..11): {ex.Message}", LogLevel.Trace);
            }

            // 3. Ensure tile (17, 11) has CentralStation action property
            if (buildings != null && 17 < buildings.LayerWidth && 11 < buildings.LayerHeight && buildings.Tiles[17, 11] != null)
            {
                buildings.Tiles[17, 11].Properties["Action"] = "CentralStation Bus";
            }
        }

        private void ApplyForestFiasaBlock(Map map)
        {
            RemoveWarpFromMapProperties(map, "Trolley_Pond");

            Layer? buildings = map.GetLayer("Buildings");
            Layer? back = map.GetLayer("Back");

            if (buildings != null)
            {
                TileSheet sheet = map.GetTileSheet("outdoors")
                               ?? map.GetTileSheet("untitled tile sheet")
                               ?? buildings.Tiles[4, 68]?.TileSheet
                               ?? map.TileSheets[0];
                int tileIndex = buildings.Tiles[4, 68]?.TileIndex ?? 103;

                for (int x = 4; x <= 6; x++)
                {
                    for (int y = 67; y <= 69; y++)
                    {
                        if (x < buildings.LayerWidth && y < buildings.LayerHeight)
                        {
                            StaticTile tile = new StaticTile(buildings, sheet, BlendMode.Alpha, tileIndex);
                            tile.Properties["Action"] = "ProgressionRebalance_FiasaBlocked";
                            tile.Properties["TouchAction"] = "ProgressionRebalance_FiasaBlocked";
                            buildings.Tiles[x, y] = tile;
                        }
                    }
                }
            }

            if (back != null)
            {
                for (int x = 4; x <= 6; x++)
                {
                    for (int y = 67; y <= 69; y++)
                    {
                        if (x < back.LayerWidth && y < back.LayerHeight && back.Tiles[x, y] != null)
                        {
                            back.Tiles[x, y].Properties.Remove("TouchAction");
                        }
                    }
                }
            }
        }

        private void ApplyBeachFiasaBlock(Map map)
        {
            RemoveWarpFromMapProperties(map, "Cove_exit");

            Layer? buildings = map.GetLayer("Buildings");
            Layer? back = map.GetLayer("Back");

            if (buildings != null)
            {
                TileSheet sheet = buildings.Tiles[10, 1]?.TileSheet
                               ?? map.GetTileSheet("untitled tile sheet")
                               ?? map.TileSheets[0];
                int tileIndex = buildings.Tiles[10, 1]?.TileIndex ?? 422;

                for (int x = 9; x <= 12; x++)
                {
                    for (int y = 0; y <= 3; y++)
                    {
                        if (x < buildings.LayerWidth && y < buildings.LayerHeight)
                        {
                            StaticTile tile = new StaticTile(buildings, sheet, BlendMode.Alpha, tileIndex);
                            tile.Properties["Action"] = "ProgressionRebalance_FiasaBlocked";
                            tile.Properties["TouchAction"] = "ProgressionRebalance_FiasaBlocked";
                            buildings.Tiles[x, y] = tile;
                        }
                    }
                }
            }

            if (back != null)
            {
                for (int x = 9; x <= 12; x++)
                {
                    for (int y = 0; y <= 3; y++)
                    {
                        if (x < back.LayerWidth && y < back.LayerHeight && back.Tiles[x, y] != null)
                        {
                            back.Tiles[x, y].Properties.Remove("TouchAction");
                        }
                    }
                }
            }
        }

        private void ApplyTunnelWalkDesertBlock(Map map)
        {
            var back = map.GetLayer("Back");
            if (back != null)
            {
                for (int y = 6; y <= 13; y++)
                {
                    for (int x = 0; x <= 1; x++)
                    {
                        if (x < back.LayerWidth && y < back.LayerHeight && back.Tiles[x, y] != null)
                        {
                            if (back.Tiles[x, y].Properties.ContainsKey("TouchAction"))
                            {
                                back.Tiles[x, y].Properties["TouchAction"] = "ProgressionRebalance_WalkDesertBlocked";
                            }
                        }
                    }
                }
            }
            RemoveWarpFromMapProperties(map, "Forest_Road");
            RemoveWarpFromMapProperties(map, "WalkToDesert");
        }

        private void ApplyBackwoodsHorseWarpRemoval(Map map)
        {
            var back = map.GetLayer("Back");
            if (back != null && 30 < back.LayerWidth && 23 < back.LayerHeight && back.Tiles[30, 23] != null)
            {
                back.Tiles[30, 23].Properties.Remove("TouchAction");
            }
            RemoveWarpFromMapProperties(map, "Forest_Road");
            RemoveWarpFromMapProperties(map, "WalkToDesert");
        }

        private void ApplyForestRoadHorseWarpRemoval(Map map)
        {
            var back = map.GetLayer("Back");
            if (back != null && 53 < back.LayerWidth && 13 < back.LayerHeight && back.Tiles[53, 13] != null)
            {
                back.Tiles[53, 13].Properties.Remove("TouchAction");
            }
            if (back != null && 50 < back.LayerWidth && 14 < back.LayerHeight && back.Tiles[50, 14] != null)
            {
                back.Tiles[50, 14].Properties.Remove("TouchAction");
            }
            RemoveWarpFromMapProperties(map, "Backwoods");
        }

        private void ApplyRailroadUnification(Map map)
        {
            var front = map.GetLayer("Front");
            var buildings = map.GetLayer("Buildings");

            // Remove the separate Grampleton Fields terminal at (30, 39..40)
            if (front != null && 30 < front.LayerWidth && 39 < front.LayerHeight)
            {
                front.Tiles[30, 39] = null;
            }
            if (buildings != null && 30 < buildings.LayerWidth && 40 < buildings.LayerHeight)
            {
                buildings.Tiles[30, 40] = null;
            }
            RemoveWarpFromMapProperties(map, "Custom_GrampletonFields");
            RemoveWarpFromMapProperties(map, "GrampletonFields");
        }

        private void ApplyGrampletonFieldsCentralStationTerminal(Map map)
        {
            var front = map.GetLayer("Front");
            var buildings = map.GetLayer("Buildings");

            // 1. Remove old handcar tiles at (86, 7) on Front and (86, 8) on Buildings
            if (front != null && 86 < front.LayerWidth && 7 < front.LayerHeight)
            {
                front.Tiles[86, 7] = null;
            }
            if (buildings != null && 86 < buildings.LayerWidth && 8 < buildings.LayerHeight)
            {
                buildings.Tiles[86, 8] = null;
            }

            // 2. Patch Central Station's ticket machine visual onto (86, 7..8)
            try
            {
                Map ticketMachineMap = Helper.GameContent.Load<Map>("Mods/Pathoschild.CentralStation/TicketMachine");
                var patchHelper = Helper.GameContent.GetPatchHelper(map, "Maps/Custom_GrampletonFields");
                patchHelper.AsMap().PatchMap(ticketMachineMap, null, new Rectangle(86, 7, 1, 2));
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not patch Central Station ticket machine visual onto Grampleton Fields: {ex.Message}", LogLevel.Trace);
            }

            // 3. Ensure tile (86, 8) has CentralStation action property
            if (buildings != null && 86 < buildings.LayerWidth && 8 < buildings.LayerHeight && buildings.Tiles[86, 8] != null)
            {
                buildings.Tiles[86, 8].Properties["Action"] = "CentralStation Train";
            }

            // 4. Set map property for Central Station
            map.Properties["Pathoschild.CentralStation_TicketMachine"] = "86 8 Train";
        }

        private void ApplyBackwoodsWalkDesertBlock(Map map)
        {
            // Restored original map: no restriction tiles or warp removals
        }

        private void ApplyTrolleyPondDesertBlock(Map map)
        {
            // Restored original map: no restriction tiles or warp removals
        }

        private void RemoveWarpFromMapProperties(Map map, string targetLocationName)
        {
            if (map.Properties.TryGetValue("Warp", out var warpProp))
            {
                string warpStr = warpProp.ToString() ?? "";
                var tokens = warpStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                for (int i = 0; i <= tokens.Count - 5; i++)
                {
                    if (tokens[i + 2].Contains(targetLocationName, StringComparison.OrdinalIgnoreCase))
                    {
                        tokens.RemoveRange(i, 5);
                        i--;
                    }
                }
                map.Properties["Warp"] = string.Join(" ", tokens);
            }
        }

        private void RemoveLiveWarps(GameLocation location, string targetLocationName)
        {
            for (int i = location.warps.Count - 1; i >= 0; i--)
            {
                var w = location.warps[i];
                if (w.TargetName != null && w.TargetName.Contains(targetLocationName, StringComparison.OrdinalIgnoreCase))
                {
                    location.warps.RemoveAt(i);
                }
            }
        }

        // --- Asset Editing ---

        private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            // 0. Unify Bus Terminal at BusStop (Late + 100 to run after Central Station's Late edit)
            if (e.NameWithoutLocale.IsEquivalentTo("Maps/BusStop"))
            {
                e.Edit(asset =>
                {
                    var map = asset.AsMap().Data;
                    ApplyBusStopUnification(map);
                }, AssetEditPriority.Late + 100);
            }

            // Clean [Bus Locations mod] / [Train Station mod] from Central Station internal translations
            if (e.NameWithoutLocale.IsEquivalentTo("Mods/Pathoschild.CentralStation/InternalTranslations"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, string>().Data;
                    data["destinations.from-bus-locations-mod"] = "{0}";
                    data["destinations.from-train-station-mod"] = "{0}";
                }, AssetEditPriority.Late + 100);
            }

            // 1. Block FIASA in Forest map asset
            if (e.NameWithoutLocale.IsEquivalentTo("Maps/Forest"))
            {
                if (Config.EnableFIASAGate && isTrolleyInstalled && !IsFIASAArrived())
                {
                    e.Edit(asset =>
                    {
                        var map = asset.AsMap().Data;
                        ApplyForestFiasaBlock(map);
                    }, AssetEditPriority.Late + 100);
                }
            }

            // 2. Block FIASA in Beach map asset
            if (e.NameWithoutLocale.IsEquivalentTo("Maps/Beach") || e.NameWithoutLocale.IsEquivalentTo("Maps/Beach-NightMarket"))
            {
                if (Config.EnableFIASAGate && isTrolleyInstalled && !IsFIASAArrived())
                {
                    e.Edit(asset =>
                    {
                        var map = asset.AsMap().Data;
                        ApplyBeachFiasaBlock(map);
                    }, AssetEditPriority.Late + 100);
                }
            }



            // Inject Willy letter
            if (e.NameWithoutLocale.IsEquivalentTo("Data/Mail"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, string>().Data;
                    data[MailWillyFiasa] = Helper.Translation.Get("willy.fiasa_letter");
                });
            }

            // Inject Aquarium Quest
            if (e.NameWithoutLocale.IsEquivalentTo("Data/Quests"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, string>().Data;
                    string title = Helper.Translation.Get("aquarium_quest.title");
                    string desc = Helper.Translation.Get("aquarium_quest.description");
                    string obj = Helper.Translation.Get("aquarium_quest.objective");
                    int reward = Config.AquariumQuestRewardGold;
                    data[QuestAquariumId] = $"Location/{title}/{desc}/{obj}/Gervig91.StardewAquariumCP_ExteriorMuseum/-1/{reward}/{reward}g/false";
                });
            }

            // Inject Willy Dialogue
            if (e.NameWithoutLocale.IsEquivalentTo("Characters/Dialogue/Willy"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, string>().Data;
                    data["ProgressionRebalance_WillyArrival"] = Helper.Translation.Get("willy.fiasa_dialogue");
                });
            }

            // Inject Inke Dialogue
            if (e.NameWithoutLocale.IsEquivalentTo("Characters/Dialogue/CC_Inke"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, string>().Data;
                    data["ProgressionRebalance_InkeAquarium"] = Helper.Translation.Get("inke.aquarium_dialogue");
                });
            }

            // Rebalance Central Station Stops & Hide Aquarium until Inke grants quest
            if (e.NameWithoutLocale.IsEquivalentTo("Mods/Pathoschild.CentralStation/Stops"))
            {
                e.Edit(asset =>
                {
                    try
                    {
                        if (asset.Data is System.Collections.IDictionary dict)
                        {
                            foreach (System.Collections.DictionaryEntry entry in dict)
                            {
                                string? key = entry.Key?.ToString();
                                object? model = entry.Value;
                                if (key == null || model == null)
                                    continue;

                                Type modelType = model.GetType();
                                PropertyInfo? condProp = modelType.GetProperty("Condition");
                                PropertyInfo? costProp = modelType.GetProperty("Cost");

                                // Lock Central Station stop condition until bus repair
                                if (Config.EnableCentralStationGate && key.EndsWith("_CentralStation"))
                                {
                                    condProp?.SetValue(model, "LOCATION_ACCESSIBLE Desert");
                                }

                                // Gate Aquarium ticket price and require Inke quest mail flag
                                if (key.Contains("Aquarium") || key.EndsWith("ExteriorMuseum"))
                                {
                                    costProp?.SetValue(model, Config.AquariumTicketPrice);
                                    condProp?.SetValue(model, "PLAYER_HAS_MAIL Current " + FlagAquariumQuestReceived);
                                }

                                // Ensure Grampleton Fields stop is accessible from Central Station
                                if (key.Contains("GrampletonFields"))
                                {
                                    condProp?.SetValue(model, null);
                                    modelType.GetProperty("Network")?.SetValue(model, "Bus, Train");
                                }
                            }

                            // Add Grampleton Fields stop to Central Station network
                            if (Config.EnableGrampletonFieldsCentralStation && !dict.Contains("ProgressionRebalance_GrampletonFields"))
                            {
                                foreach (System.Collections.DictionaryEntry entry in dict)
                                {
                                    if (entry.Value != null)
                                    {
                                        Type modelType = entry.Value.GetType();
                                        object newStop = Activator.CreateInstance(modelType)!;
                                        modelType.GetProperty("DisplayName")?.SetValue(newStop, Helper.Translation.Get("destinations.grampleton_fields"));
                                        modelType.GetProperty("ToLocation")?.SetValue(newStop, "Custom_GrampletonFields");
                                        modelType.GetProperty("ToFacingDirection")?.SetValue(newStop, "down");
                                        modelType.GetProperty("Network")?.SetValue(newStop, "Bus, Train");
                                        modelType.GetProperty("Cost")?.SetValue(newStop, 0);
                                        modelType.GetProperty("Condition")?.SetValue(newStop, null);

                                        PropertyInfo? toTileProp = modelType.GetProperty("ToTile");
                                        if (toTileProp != null)
                                        {
                                            Type tileType = toTileProp.PropertyType;
                                            if (tileType == typeof(Point))
                                            {
                                                toTileProp.SetValue(newStop, new Point(86, 9));
                                            }
                                            else if (tileType == typeof(Vector2))
                                            {
                                                toTileProp.SetValue(newStop, new Vector2(86, 9));
                                            }
                                            else
                                            {
                                                object tileObj = Activator.CreateInstance(tileType)!;
                                                tileType.GetProperty("X")?.SetValue(tileObj, 86);
                                                tileType.GetProperty("Y")?.SetValue(tileObj, 9);
                                                toTileProp.SetValue(newStop, tileObj);
                                            }
                                        }

                                        dict["ProgressionRebalance_GrampletonFields"] = newStop;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Monitor.LogOnce($"Error editing Central Station stops asset: {ex.Message}", LogLevel.Warn);
                    }
                }, AssetEditPriority.Late);
            }

            // Downtown Zuzu: Sell Qi Blade for 50 Qi Gems
            if (e.NameWithoutLocale.IsEquivalentTo("Data/Shops"))
            {
                if (Config.EnableQiBladeQiGemsPrice)
                {
                    e.Edit(asset =>
                    {
                        var data = asset.AsDictionary<string, StardewValley.GameData.Shops.ShopData>().Data;
                        if (data.TryGetValue("DTZ.DowntownZuzuSTF_SecretQi", out var shop) && shop.Items != null)
                        {
                            var item = shop.Items.FirstOrDefault(i => i.ItemId == "(W)DTZ.DowntownZuzuCP_QiBlade");
                            if (item != null)
                            {
                                item.Price = 0;
                                item.TradeItemId = "(O)858";
                                item.TradeItemAmount = Config.QiBladeQiGemsCost;
                            }

                            var sword = shop.Items.FirstOrDefault(i => i.ItemId == "(W)DTZ.DowntownZuzuCP_Serpent'sSmallsword");
                            if (sword != null)
                            {
                                sword.Price = 0;
                                sword.TradeItemId = "(O)858";
                                sword.TradeItemAmount = Config.QiBladeQiGemsCost;
                            }
                        }
                    }, AssetEditPriority.Late + 50);
                }
            }

            // Railroad Grampleton Terminal removal
            if (e.NameWithoutLocale.IsEquivalentTo("Maps/Railroad"))
            {
                if (Config.RemoveRailroadGrampletonTerminal)
                {
                    e.Edit(asset =>
                    {
                        var map = asset.AsMap().Data;
                        ApplyRailroadUnification(map);
                    }, AssetEditPriority.Late + 100);
                }
            }

            // Grampleton Fields Central Station Terminal
            if (e.NameWithoutLocale.IsEquivalentTo("Maps/Custom_GrampletonFields") || e.NameWithoutLocale.IsEquivalentTo("Maps/GrampletonFields"))
            {
                if (Config.EnableGrampletonFieldsCentralStation)
                {
                    e.Edit(asset =>
                    {
                        var map = asset.AsMap().Data;
                        ApplyGrampletonFieldsCentralStationTerminal(map);
                    }, AssetEditPriority.Late + 100);
                }
            }

            // Block Walk to Desert through Tunnel until first summer
            if (e.NameWithoutLocale.IsEquivalentTo("Maps/Tunnel"))
            {
                if (Config.EnableWalkToDesertGate && isWalkToDesertInstalled && IsBeforeFirstSummer())
                {
                    e.Edit(asset =>
                    {
                        var map = asset.AsMap().Data;
                        ApplyTunnelWalkDesertBlock(map);
                    }, AssetEditPriority.Late + 100);
                }
            }

            // Remove Horse Warp from Backwoods completely
            if (e.NameWithoutLocale.IsEquivalentTo("Maps/Backwoods"))
            {
                if (Config.RemoveHorseWarp && isWalkToDesertInstalled)
                {
                    e.Edit(asset =>
                    {
                        var map = asset.AsMap().Data;
                        ApplyBackwoodsHorseWarpRemoval(map);
                    }, AssetEditPriority.Late + 100);
                }
            }

            // Remove Horse Warp from Forest Road completely
            if (e.NameWithoutLocale.IsEquivalentTo("Maps/Tarniyar.CP.WalkToDesert_Forest_Road"))
            {
                if (Config.RemoveHorseWarp && isWalkToDesertInstalled)
                {
                    e.Edit(asset =>
                    {
                        var map = asset.AsMap().Data;
                        ApplyForestRoadHorseWarpRemoval(map);
                    }, AssetEditPriority.Late + 100);
                }
            }
        }

        // --- Multiplayer Synchronization ---

        private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != ModManifest.UniqueID)
                return;

            if (e.Type == nameof(FiasaArrivedMessage))
            {
                var msg = e.ReadAs<FiasaArrivedMessage>();
                if (msg.Arrived)
                {
                    Game1.player.mailReceived.Add(FlagFiasaArrived);
                    Game1.MasterPlayer.mailReceived.Add(FlagFiasaArrived);

                    Helper.GameContent.InvalidateCache("Maps/Forest");
                    Helper.GameContent.InvalidateCache("Maps/Beach");
                    Helper.GameContent.InvalidateCache("Maps/Beach-NightMarket");

                    Game1.getLocationFromName("Forest")?.reloadMap();
                    Game1.getLocationFromName("Beach")?.reloadMap();
                    Game1.getLocationFromName("Beach-NightMarket")?.reloadMap();

                    Monitor.Log("Received multiplayer sync: FIASA arrived. Path opened.", LogLevel.Debug);
                }
            }
            else if (e.Type == nameof(AquariumQuestStateMessage))
            {
                var msg = e.ReadAs<AquariumQuestStateMessage>();
                if (msg.Visited)
                {
                    Game1.player.mailReceived.Add(FlagAquariumVisited);
                    Game1.MasterPlayer.mailReceived.Add(FlagAquariumVisited);
                }
                if (msg.IsActive && !HasReceivedAquariumQuest())
                {
                    Game1.player.mailReceived.Add(FlagAquariumQuestReceived);
                    Game1.MasterPlayer.mailReceived.Add(FlagAquariumQuestReceived);
                    Game1.player.addQuest(QuestAquariumId);
                    Helper.GameContent.InvalidateCache("Mods/Pathoschild.CentralStation/Stops");
                }
                if (msg.IsCompleted)
                {
                    Game1.player.mailReceived.Add(FlagAquariumQuestCompleted);
                    Game1.MasterPlayer.mailReceived.Add(FlagAquariumQuestCompleted);
                    Quest? q = Game1.player.questLog.FirstOrDefault(quest => quest.id.Value == QuestAquariumId);
                    if (q != null && !q.completed.Value)
                    {
                        q.questComplete();
                    }
                }
                Monitor.Log("Received multiplayer sync: Aquarium quest state updated.", LogLevel.Debug);
            }
            else if (e.Type == nameof(ProgressionSyncMessage))
            {
                var msg = e.ReadAs<ProgressionSyncMessage>();
                if (msg.FiasaArrived)
                {
                    Game1.player.mailReceived.Add(FlagFiasaArrived);
                    Game1.MasterPlayer.mailReceived.Add(FlagFiasaArrived);

                    Helper.GameContent.InvalidateCache("Maps/Forest");
                    Helper.GameContent.InvalidateCache("Maps/Beach");
                    Helper.GameContent.InvalidateCache("Maps/Beach-NightMarket");

                    Game1.getLocationFromName("Forest")?.reloadMap();
                    Game1.getLocationFromName("Beach")?.reloadMap();
                    Game1.getLocationFromName("Beach-NightMarket")?.reloadMap();
                }
                if (msg.AquariumVisited)
                {
                    Game1.player.mailReceived.Add(FlagAquariumVisited);
                    Game1.MasterPlayer.mailReceived.Add(FlagAquariumVisited);
                }
                if (msg.AquariumQuestActive && !HasReceivedAquariumQuest())
                {
                    Game1.player.mailReceived.Add(FlagAquariumQuestReceived);
                    Game1.MasterPlayer.mailReceived.Add(FlagAquariumQuestReceived);
                    Game1.player.addQuest(QuestAquariumId);
                    Helper.GameContent.InvalidateCache("Mods/Pathoschild.CentralStation/Stops");
                }
                if (msg.AquariumQuestCompleted)
                {
                    Game1.player.mailReceived.Add(FlagAquariumQuestCompleted);
                    Game1.MasterPlayer.mailReceived.Add(FlagAquariumQuestCompleted);
                    Quest? q = Game1.player.questLog.FirstOrDefault(quest => quest.id.Value == QuestAquariumId);
                    if (q != null && !q.completed.Value)
                    {
                        q.questComplete();
                    }
                }
            }
        }

        private void OnPeerContextReceived(object? sender, PeerContextReceivedEventArgs e)
        {
            if (Context.IsMainPlayer)
            {
                var syncMsg = new ProgressionSyncMessage
                {
                    FiasaArrived = IsFIASAArrived(),
                    AquariumQuestActive = HasReceivedAquariumQuest() && !IsAquariumQuestCompleted(),
                    AquariumQuestCompleted = IsAquariumQuestCompleted(),
                    AquariumVisited = HasVisitedAquarium()
                };

                Helper.Multiplayer.SendMessage(
                    syncMsg,
                    nameof(ProgressionSyncMessage),
                    modIDs: new[] { ModManifest.UniqueID },
                    playerIDs: new[] { e.Peer.PlayerID }
                );
            }
        }

        // --- Generic Mod Config Menu ---

        private void RegisterGenericModConfigMenu()
        {
            var configMenu = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (configMenu == null)
                return;

            configMenu.Register(
                mod: ModManifest,
                reset: () => Config = new ModConfig(),
                save: () => Helper.WriteConfig(Config)
            );

            // FIASA section
            configMenu.AddSectionTitle(ModManifest, () => Helper.Translation.Get("config.fiasa_section"));
            configMenu.AddBoolOption(
                ModManifest,
                getValue: () => Config.EnableFIASAGate,
                setValue: value => Config.EnableFIASAGate = value,
                name: () => Helper.Translation.Get("config.enable_fiasa_gate"),
                tooltip: () => Helper.Translation.Get("config.enable_fiasa_gate.desc")
            );
            configMenu.AddTextOption(
                ModManifest,
                getValue: () => Config.FIASAArrivalSeason,
                setValue: value => Config.FIASAArrivalSeason = value,
                name: () => Helper.Translation.Get("config.fiasa_season"),
                allowedValues: new[] { "Spring", "Summer", "Fall", "Winter" }
            );
            configMenu.AddNumberOption(
                ModManifest,
                getValue: () => Config.FIASAArrivalYear,
                setValue: value => Config.FIASAArrivalYear = value,
                name: () => Helper.Translation.Get("config.fiasa_year"),
                min: 1,
                max: 10
            );
            configMenu.AddNumberOption(
                ModManifest,
                getValue: () => Config.FIASAArrivalDay,
                setValue: value => Config.FIASAArrivalDay = value,
                name: () => Helper.Translation.Get("config.fiasa_day"),
                min: 1,
                max: 28
            );

            // Trolley Desert section
            configMenu.AddSectionTitle(ModManifest, () => Helper.Translation.Get("config.trolley_desert_section"));
            configMenu.AddBoolOption(
                ModManifest,
                getValue: () => Config.EnableTrolleyDesertGate,
                setValue: value => Config.EnableTrolleyDesertGate = value,
                name: () => Helper.Translation.Get("config.enable_trolley_desert_gate"),
                tooltip: () => Helper.Translation.Get("config.enable_trolley_desert_gate.desc")
            );

            // Walk to Desert section
            configMenu.AddSectionTitle(ModManifest, () => Helper.Translation.Get("config.walk_desert_section"));
            configMenu.AddBoolOption(
                ModManifest,
                getValue: () => Config.EnableWalkToDesertGate,
                setValue: value => Config.EnableWalkToDesertGate = value,
                name: () => Helper.Translation.Get("config.enable_walk_desert_gate"),
                tooltip: () => Helper.Translation.Get("config.enable_walk_desert_gate.desc")
            );
            configMenu.AddBoolOption(
                ModManifest,
                getValue: () => Config.RemoveHorseWarp,
                setValue: value => Config.RemoveHorseWarp = value,
                name: () => Helper.Translation.Get("config.remove_horse_warp"),
                tooltip: () => Helper.Translation.Get("config.remove_horse_warp.desc")
            );
            configMenu.AddNumberOption(
                ModManifest,
                getValue: () => Config.WalkToDesertStaminaCost,
                setValue: value => Config.WalkToDesertStaminaCost = value,
                name: () => Helper.Translation.Get("config.walk_stamina_cost"),
                tooltip: () => Helper.Translation.Get("config.walk_stamina_cost.desc"),
                min: 0f,
                max: 100f,
                interval: 1f
            );
            configMenu.AddBoolOption(
                ModManifest,
                getValue: () => Config.WalkToDesertDrainOnReturn,
                setValue: value => Config.WalkToDesertDrainOnReturn = value,
                name: () => Helper.Translation.Get("config.walk_drain_return"),
                tooltip: () => Helper.Translation.Get("config.walk_drain_return.desc")
            );

            // Central Station & Railroad section
            configMenu.AddSectionTitle(ModManifest, () => Helper.Translation.Get("config.central_station_section"));
            configMenu.AddBoolOption(
                ModManifest,
                getValue: () => Config.EnableCentralStationGate,
                setValue: value => Config.EnableCentralStationGate = value,
                name: () => Helper.Translation.Get("config.enable_central_gate"),
                tooltip: () => Helper.Translation.Get("config.enable_central_gate.desc")
            );
            configMenu.AddBoolOption(
                ModManifest,
                getValue: () => Config.RemoveRailroadGrampletonTerminal,
                setValue: value => Config.RemoveRailroadGrampletonTerminal = value,
                name: () => Helper.Translation.Get("config.remove_railroad_terminal"),
                tooltip: () => Helper.Translation.Get("config.remove_railroad_terminal.desc")
            );
            configMenu.AddBoolOption(
                ModManifest,
                getValue: () => Config.EnableAquariumQuest,
                setValue: value => Config.EnableAquariumQuest = value,
                name: () => Helper.Translation.Get("config.enable_aquarium_quest")
            );
            configMenu.AddNumberOption(
                ModManifest,
                getValue: () => Config.AquariumQuestRewardGold,
                setValue: value => Config.AquariumQuestRewardGold = value,
                name: () => Helper.Translation.Get("config.aquarium_reward"),
                min: 0,
                max: 50000,
                interval: 50
            );
            configMenu.AddNumberOption(
                ModManifest,
                getValue: () => Config.AquariumTicketPrice,
                setValue: value => Config.AquariumTicketPrice = value,
                name: () => Helper.Translation.Get("config.aquarium_price"),
                min: 0,
                max: 10000,
                interval: 25
            );

            // Downtown Zuzu section
            configMenu.AddSectionTitle(ModManifest, () => Helper.Translation.Get("config.zuzu_section"));
            configMenu.AddBoolOption(
                ModManifest,
                getValue: () => Config.EnableQiBladeQiGemsPrice,
                setValue: value => Config.EnableQiBladeQiGemsPrice = value,
                name: () => Helper.Translation.Get("config.enable_qiblade_qigems"),
                tooltip: () => Helper.Translation.Get("config.enable_qiblade_qigems.desc")
            );
            configMenu.AddNumberOption(
                ModManifest,
                getValue: () => Config.QiBladeQiGemsCost,
                setValue: value => Config.QiBladeQiGemsCost = value,
                name: () => Helper.Translation.Get("config.qiblade_cost"),
                tooltip: () => Helper.Translation.Get("config.qiblade_cost.desc"),
                min: 1,
                max: 500,
                interval: 5
            );
        }
    }
}
