using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "widgets");

            migrationBuilder.CreateTable(
                name: "widgets",
                schema: "widgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_widgets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_widgets_name",
                schema: "widgets",
                table: "widgets",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "widgets",
                schema: "widgets");
        }
    }
}
