using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecretariArt.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimetableGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_ClassSessions_SessionId",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_SessionId_Date_StudentId",
                table: "Bookings");

            migrationBuilder.AlterColumn<int>(
                name: "SessionId",
                table: "Bookings",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "CourseId",
                table: "Bookings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Existing check-ins get the course of their class (added by hand: the column is new, and billing reads it).
            migrationBuilder.Sql(
                "UPDATE b SET CourseId = g.CourseId FROM Bookings b " +
                "JOIN ClassSessions s ON s.Id = b.SessionId JOIN CourseGroups g ON g.Id = s.GroupId;");

            migrationBuilder.CreateTable(
                name: "ScheduleDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedById = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Optimal = table.Column<bool>(type: "bit", nullable: false),
                    Milliseconds = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleDrafts_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DraftGaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DraftId = table.Column<int>(type: "int", nullable: false),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    Missing = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DraftGaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DraftGaps_ScheduleDrafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "ScheduleDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DraftSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DraftId = table.Column<int>(type: "int", nullable: false),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    ClassroomId = table.Column<int>(type: "int", nullable: false),
                    Day = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Starts = table.Column<TimeOnly>(type: "time", nullable: false),
                    Ends = table.Column<TimeOnly>(type: "time", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DraftSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DraftSessions_ScheduleDrafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "ScheduleDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_OrganizationId_CourseId",
                table: "Bookings",
                columns: new[] { "OrganizationId", "CourseId" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_SessionId_Date_StudentId",
                table: "Bookings",
                columns: new[] { "SessionId", "Date", "StudentId" },
                unique: true,
                filter: "[SessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DraftGaps_DraftId",
                table: "DraftGaps",
                column: "DraftId");

            migrationBuilder.CreateIndex(
                name: "IX_DraftSessions_DraftId",
                table: "DraftSessions",
                column: "DraftId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleDrafts_OrganizationId",
                table: "ScheduleDrafts",
                column: "OrganizationId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_ClassSessions_SessionId",
                table: "Bookings",
                column: "SessionId",
                principalTable: "ClassSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_ClassSessions_SessionId",
                table: "Bookings");

            migrationBuilder.DropTable(
                name: "DraftGaps");

            migrationBuilder.DropTable(
                name: "DraftSessions");

            migrationBuilder.DropTable(
                name: "ScheduleDrafts");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_OrganizationId_CourseId",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_SessionId_Date_StudentId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CourseId",
                table: "Bookings");

            migrationBuilder.AlterColumn<int>(
                name: "SessionId",
                table: "Bookings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_SessionId_Date_StudentId",
                table: "Bookings",
                columns: new[] { "SessionId", "Date", "StudentId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_ClassSessions_SessionId",
                table: "Bookings",
                column: "SessionId",
                principalTable: "ClassSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
