using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecretariArt.Data.Migrations
{
    /// <inheritdoc />
    public partial class RejectionReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "Organizations",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRejectedAt",
                table: "Memberships",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "Memberships",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            // Rejections made before this column existed still count as "rejected before".
            migrationBuilder.Sql("UPDATE Memberships SET LastRejectedAt = DecidedAt WHERE Status = 'Rejected'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "LastRejectedAt",
                table: "Memberships");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "Memberships");
        }
    }
}
