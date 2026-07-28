using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCardLastModifiedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_modified_by",
                table: "cards",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_modified_by",
                table: "cards");
        }
    }
}
