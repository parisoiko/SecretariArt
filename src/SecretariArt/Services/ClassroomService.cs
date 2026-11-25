using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

public record ClassroomInput(string Name, int Capacity, FloorType Floor, RoomFeatures Features, bool StepFreeAccess, string? Notes);

public interface IClassroomService
{
    Task<List<Classroom>> GetAllAsync(int organizationId);
    Task<Classroom?> FindAsync(int organizationId, int classroomId);
    Task<ServiceResult> CreateAsync(int organizationId, ClassroomInput input);
    Task<ServiceResult> UpdateAsync(int organizationId, int classroomId, ClassroomInput input);
    Task<ServiceResult> DeleteAsync(int organizationId, int classroomId);
}

// Every query filters by organisation, so a classroom id from another organisation is simply "not found".
public class ClassroomService(SecretariArtDbContext db) : IClassroomService
{
    public Task<List<Classroom>> GetAllAsync(int organizationId) =>
        db.Classrooms.AsNoTracking().Where(c => c.OrganizationId == organizationId).OrderBy(c => c.Name).ToListAsync();

    public Task<Classroom?> FindAsync(int organizationId, int classroomId) =>
        db.Classrooms.AsNoTracking().FirstOrDefaultAsync(c => c.Id == classroomId && c.OrganizationId == organizationId);

    public async Task<ServiceResult> CreateAsync(int organizationId, ClassroomInput input)
    {
        if (await NameTakenAsync(organizationId, input.Name, exceptId: null))
        {
            return NameTaken(input.Name);
        }

        var classroom = new Classroom { OrganizationId = organizationId };
        Apply(classroom, input);
        db.Classrooms.Add(classroom);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{classroom.Name} added.");
    }

    public async Task<ServiceResult> UpdateAsync(int organizationId, int classroomId, ClassroomInput input)
    {
        Classroom? classroom = await db.Classrooms.FirstOrDefaultAsync(c => c.Id == classroomId && c.OrganizationId == organizationId);
        if (classroom == null)
        {
            return NotFound;
        }
        if (await NameTakenAsync(organizationId, input.Name, classroomId))
        {
            return NameTaken(input.Name);
        }

        Apply(classroom, input);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{classroom.Name} saved.");
    }

    // Nothing refers to classrooms yet; once sessions do (milestone 3), deleting a room in use will be refused.
    public async Task<ServiceResult> DeleteAsync(int organizationId, int classroomId)
    {
        Classroom? classroom = await db.Classrooms.FirstOrDefaultAsync(c => c.Id == classroomId && c.OrganizationId == organizationId);
        if (classroom == null)
        {
            return NotFound;
        }

        db.Classrooms.Remove(classroom);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{classroom.Name} deleted.");
    }

    private static void Apply(Classroom classroom, ClassroomInput input)
    {
        classroom.Name = input.Name.Trim();
        classroom.Capacity = input.Capacity;
        classroom.Floor = input.Floor;
        classroom.Features = input.Features;
        classroom.StepFreeAccess = input.StepFreeAccess;
        classroom.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
    }

    // Ignoring case and accents (TextMatch), the same on every database: "ΧΑΡΑΚΤΙΚΗ" is "Χαρακτική".
    private async Task<bool> NameTakenAsync(int organizationId, string name, int? exceptId) =>
        (await db.Classrooms.Where(c => c.OrganizationId == organizationId && c.Id != exceptId).Select(c => c.Name).ToListAsync())
            .Any(n => TextMatch.SameName(n, name));

    private static ServiceResult NameTaken(string name) => ServiceResult.Fail($"There's already a classroom called {name.Trim()}.");

    private static readonly ServiceResult NotFound = ServiceResult.Fail("We couldn't find that classroom.");
}
