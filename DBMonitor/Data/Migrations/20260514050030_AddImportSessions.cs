using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DBMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Schema = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Table = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TempFilePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportSessions_OwnerId_ExpiresUtc",
                table: "ImportSessions",
                columns: new[] { "OwnerId", "ExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportSessions");
        }
    }
}
