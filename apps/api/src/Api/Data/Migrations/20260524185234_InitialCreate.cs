using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                });

            // Trigger function used by every IHasTimestamps table. Defined once here; future
            // entities just need their own CREATE TRIGGER referencing this function (see the
            // "Adding a new entity" checklist in .claude/skills/backend-dev/SKILL.md).
            //
            // Behavior:
            //   - CreatedAt is locked: never modifiable via UPDATE.
            //   - UpdatedAt: if the writer changed it (e.g., EF + TimestampsInterceptor),
            //     leave their value alone — app time stays authoritative for normal flow.
            //     If the writer didn't touch it (raw SQL UPDATE, future serverless worker,
            //     etc.), stamp it with now() so the column doesn't go stale.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION set_timestamps_on_update() RETURNS TRIGGER AS $$
                BEGIN
                    NEW.""CreatedAt"" := OLD.""CreatedAt"";
                    IF NEW.""UpdatedAt"" IS NOT DISTINCT FROM OLD.""UpdatedAt"" THEN
                        NEW.""UpdatedAt"" := now();
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
            ");

            migrationBuilder.Sql(@"
                CREATE TRIGGER posts_set_timestamps_on_update
                BEFORE UPDATE ON ""Posts""
                FOR EACH ROW EXECUTE FUNCTION set_timestamps_on_update();
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS posts_set_timestamps_on_update ON ""Posts"";");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS set_timestamps_on_update();");

            migrationBuilder.DropTable(
                name: "Posts");
        }
    }
}
