using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dfo.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LeadThread : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ThreadId",
                table: "Leads",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThreadId",
                table: "Leads");
        }
    }
}
