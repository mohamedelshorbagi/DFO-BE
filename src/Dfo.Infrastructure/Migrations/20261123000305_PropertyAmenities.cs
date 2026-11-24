using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dfo.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PropertyAmenities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AmenitiesJson",
                table: "Properties",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmenitiesJson",
                table: "Properties");
        }
    }
}
