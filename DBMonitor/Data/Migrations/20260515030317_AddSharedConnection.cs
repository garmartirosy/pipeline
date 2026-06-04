using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DBMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsShared",
                table: "ConnectionProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsShared",
                table: "ConnectionProfiles");
        }
    }
}
