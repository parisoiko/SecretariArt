namespace SecretariArt.Models;

// A room sessions can take place in. Courses state the features they need, so the schedule only uses suitable rooms.
public class Classroom
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public string Name { get; set; } = "";
    public int Capacity { get; set; }
    public FloorType Floor { get; set; } = FloorType.Standard;
    public RoomFeatures Features { get; set; }
    public bool StepFreeAccess { get; set; }
    public string? Notes { get; set; }
}

public enum FloorType
{
    Standard,
    SprungDanceFloor,
    Stage,
    Carpet,
    Concrete,
}

// What a room offers, and what a course needs. A room suits a course when it has every feature the course needs.
[Flags]
public enum RoomFeatures
{
    None = 0,
    Piano = 1,
    Mirrors = 2,
    Barres = 4,
    SoundSystem = 8,
    Projector = 16,
    Blackout = 32,
    StageLighting = 64,
    Easels = 128,
    Sink = 256,
    Soundproofing = 512,
}
