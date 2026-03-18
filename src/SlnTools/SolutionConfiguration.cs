using System.Collections;
using System.Xml;

namespace SlnTools;

using static SlnHelpers;

public class SolutionConfiguration
{
    public string SlnFile { get; }
    public string SlnDirectory { get; }

    public string SlnRecalculatedDirectory { get; internal set; }

    public string? SlnVersionStr
    {
        get => _SlnVersionStr;
        set
        {
            _SlnVersionStr = value;
            if (!string.IsNullOrWhiteSpace(_SlnVersionStr))
                SlnVersion = Version.Parse(_SlnVersionStr);
        }
    }

    public string? VsMajorVersionStr { get; set; }

    public Version? SlnVersion { get; private set; }
    private string? _SlnVersionStr;

    public string? VsVersionStr
    {
        get => _VsVersionStr;
        set
        {
            _VsVersionStr = value;
            if (!string.IsNullOrWhiteSpace(_VsVersionStr))
            {
                //VsVersion = Version.Parse(_VsVersionStr);
            }
        }
    }

    public Version? VsVersion { get; private set; }
    private string? _VsVersionStr;


    public string? VsMinimalVersionStr
    {
        get => _VsMinimalVersionStr;
        set
        {
            _VsMinimalVersionStr = value;
            if (!string.IsNullOrWhiteSpace(_VsMinimalVersionStr))
                VsMinimalVersion = Version.Parse(_VsMinimalVersionStr);
        }
    }

    public Version? VsMinimalVersion { get; private set; }
    private string? _VsMinimalVersionStr;
    public Projects Projects { get; } = new();
    public Sections Sections { get; } = new();

    public SolutionConfiguration(string slnFile)
    {
        SlnFile = slnFile;
        SlnDirectory = Path.GetDirectoryName(SlnFile) ?? throw new NullReferenceException();
        SlnRecalculatedDirectory = SlnDirectory;
    }
}

public class Project
{
    public string ProjectTypeGuid { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string OriginalName { get; set; } = null!;
    public string FilePath { get; set; } = null!;

    public string AbsoluteFilePath { get; set; } = null!;

    public string AbsoluteOriginalDirectory { get; set; } = null!;
    public string ProjectGuid { get; set; } = null!;

    public XmlDocument? ProjectXml { get; internal set; }

    public ProjectReferences ProjectReferences { get; } = new();
    public PackageReferences PackageReferences { get; } = new();
    public List<string> SlnLines { get; } = new();
    public override string ToString() => $"{Name} ({FilePath})";
}

public enum ReferenceType
{
    Package,
    Project
}

public interface IReference
{
    public ReferenceType ReferenceType { get; }
    public string Name { get; }
    public string AttributeName { get; }
    public XmlNode XmlNode { get; }
}

public class ReferenceComparer : IEqualityComparer<IReference>
{
    public bool Equals(IReference? x, IReference? y) => x?.Name.Equals(y?.Name) ?? y?.Name.Equals(x?.Name) ?? false;
    public int GetHashCode(IReference obj) => obj.Name.GetHashCode();
}

public abstract class ReferenceList<T> : IEnumerable<T> where T : IReference
{
    protected HashSet<T> _List = new((IEqualityComparer<T>)new ReferenceComparer());
    public XmlNode? RootNode { get; private set; }
    public int Count => RootNode?.ChildNodes.Count ?? 0;
    protected abstract T Get(XmlNode node);

    public bool Add(XmlNode node)
    {
        bool result = _List.Add(Get(node));
        if (!result)
            return result;

        if (RootNode == null)
            RootNode = node.ParentNode;
        else if (RootNode != node.ParentNode)
        {
            node.ParentNode!.RemoveChild(node);
            RootNode.AppendChild(node);
        }

        if (RootNode is not { Name: ItemGroup })
            throw new Exception("Should not happen");

        return result;
    }

    public void AddRange(IEnumerable<XmlNode> projectNodes)
    {
        foreach (XmlNode node in projectNodes)
            Add(node);
    }

    public bool Remove(T item)
    {
        RootNode!.RemoveChild(item.XmlNode);
        return _List.Remove(item);
    }

    public IEnumerator<T> GetEnumerator() => _List.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_List).GetEnumerator();
}

public class ProjectReference : IReference
{
    public ReferenceType ReferenceType => ReferenceType.Project;
    public string Name { get; }
    public string AttributeName { get; }
    public string CsprojPath { get; }
    public XmlNode XmlNode { get; }

    public ProjectReference(XmlNode projectNode)
    {
        (Name, AttributeName) = GetNameAndAttribute(projectNode);
        CsprojPath = projectNode.Attributes![IncludeAttribute]!.InnerText;
        AttributeName = IncludeAttribute;
        Name = Path.GetFileNameWithoutExtension(CsprojPath);
        XmlNode = projectNode;
    }

    public override string ToString() => Name;
}

public class ProjectReferences : ReferenceList<ProjectReference>
{
    protected override ProjectReference Get(XmlNode node) => new(node);
}

public class PackageReference : IReference
{
    public ReferenceType ReferenceType => ReferenceType.Package;
    public string Name { get; }
    public string AttributeName { get; }
    public string? Version { get; }
    public XmlNode XmlNode { get; }

    public PackageReference(XmlNode packageNode)
    {
        (Name, AttributeName) = GetNameAndAttribute(packageNode);
        Version = packageNode.Attributes!["Version"]?.InnerText;
        XmlNode = packageNode;
    }

    public override string ToString() => Name;
}

public class PackageReferences : ReferenceList<PackageReference>
{
    protected override PackageReference Get(XmlNode node) => new(node);
}

public class Projects : List<Project>
{
}

public class Section
{
    public string Name { get; set; } = null!;
    public bool IsPreSolution { get; set; }

    public List<string> Lines { get; } = new();
}

public class Sections : List<Section>
{
}