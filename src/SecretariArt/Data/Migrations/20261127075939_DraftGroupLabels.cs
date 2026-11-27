using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecretariArt.Data.Migrations
{
    /// <inheritdoc />
    public partial class DraftGroupLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GroupLabel",
                table: "DraftSessions",
                type: "nvarchar(250)",
                maxLength: 250,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GroupLabel",
                table: "DraftSessions");
        }
    }
}
