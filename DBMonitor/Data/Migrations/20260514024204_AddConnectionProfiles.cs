using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DBMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConnectionProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    EncryptedConnectionString = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectionProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionProfiles_OwnerId",
                table: "ConnectionProfiles",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectionProfiles");
        }
    }
}
