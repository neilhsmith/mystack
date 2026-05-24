using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddXminConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty. EF generated AddColumn("xmin") because the convention
            // in AppDbContext.OnModelCreating declares a shadow property mapped to "xmin",
            // but `xmin` is a Postgres SYSTEM column that already exists on every table —
            // applying the AddColumn would fail. The model snapshot still records the
            // shadow property so future migrations diff correctly; no DDL is required.
            //
            // Same applies to every future entity: the convention loop stamps `xmin` on
            // each new entity, the migration generator will emit a spurious AddColumn,
            // and the corresponding migration body should be emptied like this one.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — see Up().
        }
    }
}
