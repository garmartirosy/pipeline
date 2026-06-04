using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DBMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryAuditEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QueryAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Sql = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    RecordsAffected = table.Column<int>(type: "int", nullable: true),
                    ElapsedMs = table.Column<long>(type: "bigint", nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    RolledBack = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QueryAuditEntries_OwnerId_ExecutedUtc",
                table: "QueryAuditEntries",
                columns: new[] { "OwnerId", "ExecutedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QueryAuditEntries");
        }
    }
}
