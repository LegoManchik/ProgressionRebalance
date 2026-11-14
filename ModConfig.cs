namespace ModpackProgressionRebalance
{
    public class ModConfig
    {
        // 1. FIASA Gate
        public bool EnableFIASAGate { get; set; } = true;
        public string FIASAArrivalSeason { get; set; } = "Summer";
        public int FIASAArrivalYear { get; set; } = 1;
        public int FIASAArrivalDay { get; set; } = 1;

        // 2. Trolley Desert Gate
        public bool EnableTrolleyDesertGate { get; set; } = false;

        // 3. Walk to Desert Gate
        public bool EnableWalkToDesertGate { get; set; } = false;
        public float WalkToDesertStaminaCost { get; set; } = 0f;
        public bool WalkToDesertDrainOnReturn { get; set; } = false;

        // 4. Central Station Gate
        public bool EnableCentralStationGate { get; set; } = true;

        // 5. Aquarium Quest & Balance
        public bool EnableAquariumQuest { get; set; } = true;
        public int AquariumQuestRewardGold { get; set; } = 500;
        public int AquariumTicketPrice { get; set; } = 500;
    }
}
