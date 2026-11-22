namespace LiveBoard.Domain;

/// <summary>Role a user holds on a board. Higher values include the permissions of lower ones.</summary>
public enum BoardRole
{
    Viewer = 0,
    Editor = 1,
    Owner = 2,
}

public static class BoardRoleExtensions
{
    public static bool CanEdit(this BoardRole role) => role >= BoardRole.Editor;
    public static bool CanManage(this BoardRole role) => role >= BoardRole.Owner;
}
