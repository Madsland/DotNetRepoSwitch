using System.Text;
using System.Xml;

namespace SlnTools;

using static SlnHelpers;

public static class SlnMerger
{
    public static SolutionConfiguration Merge(IEnumerable<string> toMergePaths)
        => Merge(toMergePaths.Select(SlnParser.ParseConfiguration));

    public static SolutionConfiguration Merge(IEnumerable<SolutionConfiguration> toMerge)
    {
        List<SolutionConfiguration> toMergeList = toMerge.ToList();

        if (toMergeList.Count == 0)
            throw new Exception("At least one Solution is needed");

        Console.Write($"Merging {toMergeList.Count} sln files. ");
        SolutionConfiguration result = MergeSolutions(toMergeList);
        Console.WriteLine("Done");

        Console.Write("Replacing Packages by projects, adding dependant projects. ");
        foreach (Project project in result.Projects)
            if (project.ProjectXml != null)
            {
                XmlNode? refParent = project.ProjectReferences.RootNode;
                if (refParent == null)
                    refParent = project.ProjectXml.CreateNode(XmlNodeType.Element, ItemGroup, null);
                else
                    project.ProjectXml.DocumentElement!.RemoveChild(refParent);
                XmlNode rootNode = project.ProjectXml.RetrieveNodes("PropertyGroup").LastOrDefault()
                                   ?? project.PackageReferences.RootNode ?? throw new NullReferenceException();
                rootNode.ParentNode!.InsertAfter(refParent, rootNode);

                if (refParent.Name != ItemGroup)
                    throw new Exception($"refParent name should be {ItemGroup}");

                AddProjectReferences(result, project, project, refParent);
            }

        Console.WriteLine("Done");

        Console.Write("Cleaning packages. ");
        foreach (Project project in result.Projects)
        foreach (PackageReference package in project.PackageReferences.ToList())
            if (result.Projects.Any(p => p.OriginalName == package.Name))
                project.PackageReferences.Remove(package);
        Console.WriteLine("Done");

        return result;
    }

    private static SolutionConfiguration MergeSolutions(List<SolutionConfiguration> toMerge)
    {
        SolutionConfiguration result = toMerge.First().Clone();
        List<string> commonPathParts = Path.GetDirectoryName(result.SlnFile)!.Split('\\', '/').ToList();

        foreach (SolutionConfiguration conf in toMerge.Skip(1))
            if (result.SlnVersionStr != conf.SlnVersionStr)
                throw new NotImplementedException();
            else
            {
                if (result.VsMinimalVersion < conf.VsMinimalVersion)
                    result.VsMinimalVersionStr = conf.VsMinimalVersionStr;
                if (result.VsVersion > conf.VsVersion)
                    result.VsVersionStr = conf.VsVersionStr;
                List<string> partsResult = new();
                List<string> partsConf = Path.GetDirectoryName(conf.SlnFile)!.Split('\\', '/').ToList();
                int minLength = Math.Min(partsConf.Count, commonPathParts.Count);
                for (int i = 0; i < minLength; i++)
                    if (commonPathParts[i] == partsConf[i])
                        partsResult.Add(partsConf[i]);
                    else
                        break;
                commonPathParts = partsResult;
                foreach (Project project in conf.Projects)
                {
                    Project copy = project.Clone();
                    bool isDuplicated = result.Projects.Any(p => StringComparer.OrdinalIgnoreCase.Equals(project.Name, p.Name));
                    result.Projects.Add(copy);

                    if (!isDuplicated)
                        continue;

                    string newName = copy.Name + '_' + Path.GetFileNameWithoutExtension(conf.SlnFile);
                    copy.FilePath = copy.FilePath.Replace(copy.Name, newName);
                    copy.AbsoluteFilePath = copy.AbsoluteFilePath.Replace(copy.Name, newName);
                    copy.Name = newName;
                }

                foreach (Section section in conf.Sections)
                {
                    Section? exists = result.Sections.SingleOrDefault(s => StringComparer.OrdinalIgnoreCase.Equals(s.Name, section.Name));
                    if (exists == null)
                    {
                        exists = section.Clone(false);
                        result.Sections.Add(exists);
                    }

                    bool isConfSection = section.Name switch
                    {
                        "SolutionConfigurationPlatforms"
                            or "ProjectConfigurationPlatforms"
                            or "NestedProjects"
                            or "MonoDevelopProperties"
                            or "ExtensibilityGlobals" => false
                        , "SolutionProperties" => true
                        , _ => throw new NotImplementedException()
                    };

                    foreach (string line in section.Lines)
                        if (isConfSection)
                        {
                            string[] lineParts = line.Split('=');
                            string key = lineParts[0];
                            string? existsLine = exists.Lines.SingleOrDefault(l => l.StartsWith(key));
                            if (existsLine != null)
                            {
                                string value = existsLine.Split('=')[1].Trim();
                                if (value != lineParts[1].Trim())
                                    throw new NotImplementedException();
                            }
                        }
                        else if (!exists.Lines.Contains(line))
                            exists.Lines.Add(line);
                }
            }

        result.SlnRecalculatedDirectory = string.Join(Path.DirectorySeparatorChar, commonPathParts);

        return result;
    }

    private static void AddProjectReferences(
        SolutionConfiguration result
        , Project rootProject
        , Project currentProject
        , XmlNode refParent)
    {
        foreach (PackageReference package in currentProject.PackageReferences)
            AddProjectReferences(result, rootProject, refParent, package);

        foreach (ProjectReference project in currentProject.ProjectReferences.ToList())
            AddProjectReferences(result, rootProject, refParent, project);
    }

    private static void AddProjectReferences(
        SolutionConfiguration result
        , Project rootProject
        , XmlNode refParent
        , IReference reference)
    {
        if (rootProject.ProjectXml == null)
            throw new Exception("Should not happen");

        Project? proj = result.Projects.SingleOrDefault(p => p.Name == reference.Name);
        if (proj == null)
            return;

        XmlNode newNode = rootProject.ProjectXml.CreateNode(XmlNodeType.Element, SlnHelpers.ProjectReference, null);
        XmlAttribute attr = rootProject.ProjectXml.CreateAttribute(IncludeAttribute);
        for (int i = 0; i < rootProject.FilePath.Count(c => c is '\\' or '/'); i++)
            attr.Value += $"..{Path.DirectorySeparatorChar}";
        attr.Value += proj.FilePath;
        newNode.Attributes!.Append(attr);
        refParent.AppendChild(newNode);
        // Add need Parent to work properly
        if (!rootProject.ProjectReferences.Add(newNode))
            refParent.RemoveChild(newNode);
        AddProjectReferences(result, rootProject, proj, refParent);
    }


    public static void WriteTo(
        string destinationSlnFilePath
        , SolutionConfiguration conf
        , bool copySlnFolderFiles
        , List<FileReplacement>? fileToCopySource)
    {
        Dictionary<string, string?> fileToCopySourceDic = fileToCopySource?.ToDictionary(
                                                              c => c.CsprojFilePath ?? throw new NullReferenceException()
                                                              , c => c.ReplaceWithFilePath)
                                                          ?? new Dictionary<string, string?>();


        if (!destinationSlnFilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            destinationSlnFilePath = Path.Combine(destinationSlnFilePath, "Merged.sln");

        Console.WriteLine("Writing result sln file and projects. ");
        string toSlnDir = Path.GetDirectoryName(destinationSlnFilePath) ?? throw new NullReferenceException();
        if (!Directory.Exists(toSlnDir))
            Directory.CreateDirectory(toSlnDir);


        SlnParser.WriteConfiguration(conf, destinationSlnFilePath);

        if (copySlnFolderFiles)
        {
            Console.WriteLine("Copying solution directory files");
            string sourceSlnName = Path.GetFileNameWithoutExtension(conf.SlnFile);
            string newSlnName = Path.GetFileNameWithoutExtension(destinationSlnFilePath);
            foreach (string file in Directory.EnumerateFiles(conf.SlnDirectory))
                if (file != conf.SlnFile)
                {
                    string fileName = Path.GetFileName(file).Replace(sourceSlnName, newSlnName);
                    Console.Write($"{fileName} ");
                    string dest = Path.Combine(toSlnDir, fileName);
                    CopyWithBackup(file, dest, false);
                }

            Console.WriteLine();
        }

        XmlWriterSettings settings = new() 
        { 
            Indent = true
            , Encoding = Encoding.UTF8 
            , OmitXmlDeclaration = false
        };
        Console.WriteLine($"Merging projects to {destinationSlnFilePath}");
        foreach (Project project in conf.Projects)
            if (project.ProjectXml != null)
            {
                Console.Write(project.Name + ' ');

                StringComparer comparer = Environment.OSVersion.Platform.ToString().StartsWith("Win")
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;

                string toProjFile = Path.Combine(toSlnDir, project.FilePath.TrimStart('.', '\\', '/'));
                string toProjDir = Path.GetDirectoryName(toProjFile)!;
                if (!Directory.Exists(toProjDir))
                    Directory.CreateDirectory(toProjDir);

                WriteProjectConfiguration proj = new()
                {
                    Project = project
                    , ReplaceDic = fileToCopySourceDic
                    , Destination = Path.GetDirectoryName(destinationSlnFilePath) ?? throw new NullReferenceException("No directory to sln path")
                    , RelativePath = Path.GetRelativePath(toProjDir, project.AbsoluteOriginalDirectory)
                };

                XmlDocument newProj = new();
                CopyProject(proj, project.ProjectXml, newProj);

                using MemoryStream ms = new ();
                using XmlWriter xmlWriter = XmlWriter.Create(ms, settings);
                newProj.WriteTo(xmlWriter);
                xmlWriter.Flush();
                File.WriteAllBytes(toProjFile, ms.ToArray());
            }

        Console.WriteLine();
        Console.WriteLine("Done");
    }

    private static void CopyProject(WriteProjectConfiguration proj, XmlNode from, XmlNode to)
    {
        XmlDocument toDoc = to.OwnerDocument ?? (XmlDocument)to;
        if (from.Name == "Project")
        {
            // <Content Include="..\..\MyContentFiles\**\*.*"><Link>%(RecursiveDir)%(Filename)%(Extension)</Link></Content>

            AddRecursiveReferences(proj, to, toDoc, "Compile", "cs");
            AddRecursiveReferences(proj, to, toDoc, "Page", "xaml");
        }

        foreach (XmlNode child in from.ChildNodes)
            switch (child)
            {
                case XmlElement element:
                    XmlNode copy = toDoc.CreateElement(element.Name);
                    AddLinkIfEmbeddedRessource(element, toDoc, copy);
                    to.AppendChild(copy);

                    foreach (XmlAttribute attrFrom in element.Attributes!)
                    {
                        XmlAttribute attrTo = toDoc.CreateAttribute(attrFrom.Name);
                        attrTo.Value = attrFrom.Value;
                        copy.Attributes!.Append(attrTo);
                    }

                    TryReplaceFileAttribute(proj, copy, UpdateAttribute);
                    TryReplaceFileAttribute(proj, copy, IncludeAttribute);
                    TryReplaceFileAttribute(proj, copy, RemoveAttribute);

                    CopyProject(proj, element, copy);
                    break;
                case XmlText:
                    to.InnerText = from.InnerText;
                    break;
            }
    }

    private static void AddRecursiveReferences(WriteProjectConfiguration proj, XmlNode to, XmlDocument toDoc, string elementName, string fileExt)
    {
        fileExt = fileExt.Trim('.');
        XmlElement group = toDoc.CreateElement("ItemGroup");
        to.AppendChild(group);
        XmlNode elt = toDoc.CreateElement(elementName);
        group.AppendChild(elt);
        XmlAttribute attr = toDoc.CreateAttribute(IncludeAttribute);
        attr.Value = Path.Combine(proj.RelativePath, "**", $"*.{fileExt}");
        elt.Attributes!.Append(attr);
        XmlNode link = toDoc.CreateElement("Link");
        elt.AppendChild(link);
        link.InnerText = "%(RecursiveDir)%(Filename)%(Extension)";

        elt = toDoc.CreateElement(elementName);
        group.AppendChild(elt);
        attr = toDoc.CreateAttribute(RemoveAttribute);
        attr.Value = Path.Combine(proj.RelativePath, "bin", "**", $"*.{fileExt}");
        elt.Attributes!.Append(attr);

        elt = toDoc.CreateElement(elementName);
        group.AppendChild(elt);
        attr = toDoc.CreateAttribute(RemoveAttribute);
        attr.Value = Path.Combine(proj.RelativePath, "obj", "**", $"*.{fileExt}");
        elt.Attributes!.Append(attr);
    }

    private static void AddLinkIfEmbeddedRessource(XmlElement element, XmlDocument toDoc, XmlNode copy)
    {
        if (element.Name == EmbeddedResource)
        {
            XmlElement link = toDoc.CreateElement(Link);
            string? includeValue = element.Attributes[IncludeAttribute]?.Value;
            if (!string.IsNullOrEmpty(includeValue))
            {
                link.InnerText = includeValue;
                copy.AppendChild(link);
            }
        }
    }

    private static void TryReplaceFileAttribute(WriteProjectConfiguration proj, XmlNode node, string attrName)
    {
        if (node.Name == SlnHelpers.PackageReference
            || node.Name == SlnHelpers.ProjectReference
            || node.Name == "FrameworkReference"
            || node.Attributes is null)
            return;

        XmlAttribute? attr = node.Attributes[attrName];
        if (string.IsNullOrWhiteSpace(attr?.Value))
            return;

        if (proj.ReplaceDic.TryGetValue(attr.Value, out string? source))
            if (!string.IsNullOrWhiteSpace(source))
            {
                string copyTo = Path.Combine(proj.Destination, proj.Project.Name, attr.Value);
                CopyWithBackup(source, copyTo, true);
            }
            else
                Console.WriteLine($"Copy of {attr.Value} is ignored.");
        else
            attr.Value = Path.Combine(
                proj.RelativePath
                , attr.Value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
    }


    private static void CopyWithBackup(string origin, string destination, bool replaceDestination)
    {
        origin = origin.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        destination = destination.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string destDir = Path.GetDirectoryName(destination) ?? throw new NullReferenceException($"{destination} has no root directory");
        if (!File.Exists(destination))
        {
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);
            File.Copy(origin, destination);
            return;
        }

        byte[] sourceContent = File.ReadAllBytes(origin);
        if (IsSameContent(sourceContent, destination))
            return;

        byte[] toCompareContent = replaceDestination ? File.ReadAllBytes(destination) : sourceContent;
        string fileName = Path.GetFileNameWithoutExtension(destination) + '*';
        bool copied = false;
        foreach (string exists in Directory.EnumerateFiles(destDir, fileName))
            if (exists != destination && IsSameContent(toCompareContent, exists))
            {
                copied = true;
                break;
            }

        if (replaceDestination)
        {
            if (!copied)
                File.Copy(destination, destination + $"_{DateTime.Now:yyyyMMdd_HHmmss}.back");
            File.Copy(origin, destination, true);
        }
        else if (!copied)
            File.Copy(origin, destination + $"_{DateTime.Now:yyyyMMdd_HHmmss}.source");
    }

    private static bool IsSameContent(byte[] original, string toTest)
    {
        byte[] toTestContent = File.ReadAllBytes(toTest);
        if (original.Length != toTestContent.Length)
            return false;
        for (int i = 0; i < original.Length; i++)
            if (original[i] != toTestContent[i])
                return false;
        return true;
    }

    private static IEnumerable<string> GetToCopy(XmlDocument xmlDoc, string prefix)
    {
        foreach (XmlNode node in xmlDoc.RetrieveNodes("CopyToOutputDirectory"))
        {
            string toCopy = prefix + (node.ParentNode?.Attributes?["Update"]?.Value ?? node.ParentNode?.Attributes?["Include"]?.Value);
            if (toCopy != prefix && toCopy != "Never")
                yield return toCopy;
        }
    }
}

internal class WriteProjectConfiguration
{
    public required Project Project { get; set; }
    public required Dictionary<string, string?> ReplaceDic { get; set; }
    public required string Destination { get; set; }
    public required string RelativePath { get; set; }
}
