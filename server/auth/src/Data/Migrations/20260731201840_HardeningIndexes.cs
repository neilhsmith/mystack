using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyStack.Auth.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardeningIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                schema: "auth",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "auth",
                table: "users",
                column: "normalized_email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oidc_tokens_creation_date",
                schema: "auth",
                table: "oidc_tokens",
                column: "creation_date");

            migrationBuilder.CreateIndex(
                name: "ix_oidc_tokens_subject",
                schema: "auth",
                table: "oidc_tokens",
                column: "subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                schema: "auth",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_oidc_tokens_creation_date",
                schema: "auth",
                table: "oidc_tokens");

            migrationBuilder.DropIndex(
                name: "ix_oidc_tokens_subject",
                schema: "auth",
                table: "oidc_tokens");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "auth",
                table: "users",
                column: "normalized_email");
        }
    }
}
