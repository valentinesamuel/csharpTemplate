namespace Acme.Service.Modules.Auth.Domain;

/// <summary>A permission of the code-owned catalog (plan §0.8), stored in <c>auth.permissions</c>.</summary>
internal sealed class Permission
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 500;

    /// <summary>Maximum length of <see cref="Module"/>.</summary>
    public const int ModuleMaxLength = 63;

    public Permission(string name, string description, string module)
    {
        Name = name;
        Description = description;
        Module = module;
    }

    /// <summary>Lower-case dotted name; the primary key.</summary>
    public string Name { get; private set; }

    /// <summary>Human-readable description.</summary>
    public string Description { get; set; }

    /// <summary>The name of the module that declares the permission.</summary>
    public string Module { get; set; }
}
