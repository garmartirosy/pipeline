using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DBMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedQueriesAndPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPinned",
                table: "ConnectionProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "ConnectionProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SavedQueries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Sql = table.Column<string>(type: "nvarchar(max)", maxLength: 50000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UseCount = table.Column<int>(type: "int", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedQueries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Theme = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DefaultPageSize = table.Column<int>(type: "int", nullable: false),
                    DefaultQueryTimeout = table.Column<int>(type: "int", nullable: false),
                    DefaultMaxRows = table.Column<int>(type: "int", nullable: false),
                    ConfirmDestructiveByDefault = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.OwnerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedQueries_OwnerId_ProfileId",
                table: "SavedQueries",
                columns: new[] { "OwnerId", "ProfileId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedQueries");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "IsPinned",
                table: "ConnectionProfiles");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "ConnectionProfiles");
        }
    }
}
