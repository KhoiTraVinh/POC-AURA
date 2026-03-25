using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POC.AURA.Api.Migrations
{
    /// <summary>
    /// Superseded by <see cref="CleanupAndAddRequestorUserId"/> (20260325000003).
    /// Kept as an empty pass-through so existing <c>__EFMigrationsHistory</c> entries
    /// referencing this ID remain valid.
    /// </summary>
    [Migration("20260325000002_AddRequestorUserIdToMessages")]
    public partial class AddRequestorUserIdToMessages : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) { }
        protected override void Down(MigrationBuilder migrationBuilder) { }
    }
}
