namespace ModpackProgressionRebalance
{
    public class FiasaArrivedMessage
    {
        public bool Arrived { get; set; } = true;
    }

    public class AquariumQuestStateMessage
    {
        public bool IsActive { get; set; }
        public bool IsCompleted { get; set; }
        public bool Visited { get; set; }
    }

    public class ProgressionSyncMessage
    {
        public bool FiasaArrived { get; set; }
        public bool AquariumQuestActive { get; set; }
        public bool AquariumQuestCompleted { get; set; }
        public bool AquariumVisited { get; set; }
    }
}
