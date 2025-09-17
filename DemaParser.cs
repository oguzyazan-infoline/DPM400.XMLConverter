using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace DEMA_Parser
{
    public static class DemaProject
    {
        private static readonly Dictionary<string, string> FriendlyNames = new Dictionary<string, string>
        {
            { "Mod", "Operation" }, { "Beh", "Behavior" }, { "Health", "Health Status" }, { "NamPlt", "Name Plate" },
            { "ProOff", "Protection Off" }, { "Blk", "Block" },
            { "StrVal", "Start value" }, { "StrValMul", "Start value multiplier" }, { "OpDlTmms", "Operate delay time" },
            { "RsDlTmms", "Reset delay time" }, { "TmMult", "Time multiplier" }, { "TmACrv", "Operating curve type" },
            { "TypRsCrv", "Reset curve type" }, { "DirMod", "Directional mode" }, { "TorqueAng", "Torque Angle" },
            { "Hysteresis", "Hysteresis" }, { "MeasMode", "Measurement Mode" }, { "Str", "Start Signal" }, { "Op", "Operate Signal" },
            { "AlmVal", "Alarm Value" }, { "ConsTms1", "Time Constant" }, { "InitPer", "Initial Thermal Percentage" },
            { "TripVal", "Trip Value" }, { "AlmThm", "Thermal Alarm" },
            { "DfdtCyclesNb", "Number of cycles for df/dt" }, { "DfdtValidNb", "Valid cycles for df/dt" },
            { "InhDfdtOv20", "Inhibit df/dt over 20 Hz/s" },
            { "DirAng", "Directional Angle" },
            { "Pos", "Position" }, { "BlkOpn", "Block Opening" }, { "BlkCls", "Block Closing" },
            { "OpCnt", "Operation Counter" },
            { "TotW", "Total Active Power" }, { "TotVAr", "Total Reactive Power" }, { "TotVA", "Total Apparent Power" },
            { "TotPF", "Total Power Factor" }, { "A", "Phase Currents" }, { "PPV", "Phase-to-Phase Voltage" },
            { "PNV", "Phase-to-Neutral Voltage" }, { "Hz", "Frequency" },
            { "MemRs", "Reset Memory" }, { "RcdMade", "Record Made" }, { "FltNum", "Fault Number" },
            // --- SGCB için yeni eklenen isimler ---
            { "SGCB", "Setting Group Control" },
            { "ActSG", "Active Setting Group" },
            { "EditSG", "Edit Setting Group" },
            { "CnfEdit", "Confirm Edit" },
            { "NumOfSG", "Number of Setting Groups" },
            { "LActTm", "Last Activation Time" },
            { "ResvTms", "Reservation Timeout" }
        };

        public static XDocument ConvertDpmToHierarchical(string dpmSclContent)
        {
            XDocument dpmDoc = XDocument.Parse(dpmSclContent, LoadOptions.None);
            XNamespace ns = dpmDoc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            if (ns == XNamespace.None || dpmDoc.Root == null) return new XDocument(new XElement("Error", "Invalid SCL file format."));

            var outputRoot = new XElement("IEC61850Parameters", new XAttribute("Model", "DEMA_DPM400D_08"));
            var outputDoc = new XDocument(outputRoot);

            var lNodeTypes = dpmDoc.Descendants(ns + "LNodeType").ToDictionary(x => x.Attribute("id")?.Value ?? "", x => x);
            var doTypes = dpmDoc.Descendants(ns + "DOType").ToDictionary(x => x.Attribute("id")?.Value ?? "", x => x);
            var enumTypes = dpmDoc.Descendants(ns + "EnumType").ToDictionary(x => x.Attribute("id")?.Value ?? "", x => x);
            var daTypes = dpmDoc.Descendants(ns + "DAType").ToDictionary(x => x.Attribute("id")?.Value ?? "", x => x);

            string iedPrefix = "A612H04"; 

            foreach (var lDevice in dpmDoc.Descendants(ns + "LDevice"))
            {
                string lDeviceInst = lDevice.Attribute("inst")?.Value ?? "UnknownLDevice";
                string fullLdName = iedPrefix + lDeviceInst;

                var lDeviceGroup = new XElement("Group", new XAttribute("Name", $"LDevice - {fullLdName}"));
                outputRoot.Add(lDeviceGroup);

                foreach (var ln in lDevice.Elements(ns + "LN").Concat(lDevice.Elements(ns + "LN0")))
                {
                    string lnType = ln.Attribute("lnType")?.Value ?? "";
                    if (string.IsNullOrEmpty(lnType) || !lNodeTypes.ContainsKey(lnType)) continue;

                    var lnTypeDef = lNodeTypes[lnType];
                    string prefix = ln.Attribute("prefix")?.Value ?? "";
                    string lnClass = ln.Attribute("lnClass")?.Value ?? "";
                    string inst = ln.Attribute("inst")?.Value ?? "";
                    string functionBlockName = $"{prefix}{lnClass}{inst}";

                    var functionBlockGroup = new XElement("Group", new XAttribute("Name", functionBlockName), new XAttribute("ParameterGroup", "true"));
                    lDeviceGroup.Add(functionBlockGroup);

                    var settingsContainer = new XElement("Group", new XAttribute("Name", "General Settings"));
                    var controlsContainer = new XElement("Group", new XAttribute("Name", "Controls"));
                    var statusContainer = new XElement("Group", new XAttribute("Name", "Status"));
                    var measurementsContainer = new XElement("Group", new XAttribute("Name", "Measurements"));

                    foreach (var doElement in lnTypeDef.Elements(ns + "DO"))
                    {
                        string doTypeId = doElement.Attribute("type")?.Value ?? "";
                        if (doTypes.TryGetValue(doTypeId, out var doTypeDef))
                        {
                            ProcessDataObjectRecursive(ln, doElement, doTypeDef, "", functionBlockGroup, settingsContainer, controlsContainer, statusContainer, measurementsContainer, doTypes, daTypes, enumTypes, ns, fullLdName);
                        }
                    }
                    
                    if (functionBlockName == "LLN0" && fullLdName == "A612H04PR")
                    {
                        AddManualSGCBParameters(settingsContainer, fullLdName, functionBlockName);
                    }

                    if (settingsContainer.HasElements) functionBlockGroup.Add(settingsContainer);
                    if (controlsContainer.HasElements) functionBlockGroup.Add(controlsContainer);
                    if (statusContainer.HasElements) functionBlockGroup.Add(statusContainer);
                    if (measurementsContainer.HasElements) functionBlockGroup.Add(measurementsContainer);
                }
            }

            PruneEmptyGroups(outputRoot);
            return outputDoc;
        }

        private static void ProcessDataObjectRecursive(XElement ln, XElement doOrSdoElement, XElement typeDef, string currentPath, XElement functionBlockGroup, XElement settings, XElement controls, XElement status, XElement measurements, Dictionary<string, XElement> doTypes, Dictionary<string, XElement> daTypes, Dictionary<string, XElement> enumTypes, XNamespace ns, string fullLdName)
        {
            string name = doOrSdoElement.Attribute("name")?.Value ?? "";
            string newPath = string.IsNullOrEmpty(currentPath) ? name : $"{currentPath}.{name}";

            foreach (var da in typeDef.Elements(ns + "DA"))
            {
                string fc = da.Attribute("fc")?.Value ?? "";
                XElement? container = null;

                switch (fc)
                {
                    case "SE":
                    case "SP": container = settings; break;
                    case "CO": container = controls; break;
                    case "ST": container = status; break;
                    case "MX": container = measurements; break;
                }

                if (container != null)
                {
                    if (fc == "SE")
                    {
                        var settingGroup = FindOrCreateGroup(functionBlockGroup, new[] { "Settings", "Setting Group 1" });
                        CreateParameter(settingGroup, 1, fullLdName, ln, newPath, da, daTypes, enumTypes, ns);
                    }
                    else
                    {
                        CreateParameter(container, null, fullLdName, ln, newPath, da, daTypes, enumTypes, ns);
                    }
                }
            }

            foreach (var sdo in typeDef.Elements(ns + "SDO"))
            {
                string sdoTypeId = sdo.Attribute("type")?.Value ?? "";
                if (doTypes.TryGetValue(sdoTypeId, out var sdoTypeDef))
                {
                    ProcessDataObjectRecursive(ln, sdo, sdoTypeDef, newPath, functionBlockGroup, settings, controls, status, measurements, doTypes, daTypes, enumTypes, ns, fullLdName);
                }
            }
        }

        private static void CreateParameter(XElement parentElement, int? groupNo, string fullLdName, XElement ln, string doPath, XElement daElement, Dictionary<string, XElement> daTypes, Dictionary<string, XElement> enumTypes, XNamespace ns)
        {
            if (ln.Parent == null) return;

            string prefix = ln.Attribute("prefix")?.Value ?? "";
            string lnClass = ln.Attribute("lnClass")?.Value ?? "";
            string lnInst = ln.Attribute("inst")?.Value ?? "";
            string daName = daElement.Attribute("name")?.Value ?? "";
            string bType = daElement.Attribute("bType")?.Value ?? "";
            string typeAttr = daElement.Attribute("type")?.Value ?? "";
            string fc = daElement.Attribute("fc")?.Value ?? "";

            string objectAddress = $"{fullLdName}/{prefix}{lnClass}{lnInst}.{doPath}.{daName}";
            string baseName = doPath.Split('.').First();
            
            string friendlyName;
            if (FriendlyNames.ContainsKey(daName))
            {
                friendlyName = FriendlyNames[daName];
            }
            else
            {
                friendlyName = FriendlyNames.ContainsKey(baseName) ? FriendlyNames[baseName] : baseName;
                if (doPath.Contains('.'))
                {
                    friendlyName += $" ({doPath.Split('.').Last()}.{daName})";
                }
                else if (daName != "setVal" && daName != "ctlVal" && daName != "general" && daName != "stVal" && daName != "mag")
                {
                    friendlyName += $" ({daName})";
                }
            }

            string finalDataType = DetermineCorrectDataType(fc, daName, bType, objectAddress);

            var parameter = new XElement("Parameter",
                new XElement("Name", friendlyName),
                new XElement("ObjectAddress", objectAddress),
                new XElement("FunctionalConstraint", fc)
            );

            if (groupNo.HasValue)
            {
                parameter.Add(new XElement("GroupNo", groupNo.Value));
            }

            parameter.Add(new XElement("DataType", finalDataType));

            if (finalDataType == "Enumeration" && !string.IsNullOrEmpty(typeAttr))
            {
                string finalEnumTypeID = typeAttr;
                if (!enumTypes.ContainsKey(finalEnumTypeID) && daTypes.TryGetValue(finalEnumTypeID, out var daTypeDef))
                {
                    var bda = daTypeDef.Elements(ns + "BDA")
                                       .FirstOrDefault(b => (b.Attribute("name")?.Value == "ctlVal" || b.Attribute("name")?.Value == "stVal") && b.Attribute("bType")?.Value == "Enum");
                    if (bda != null)
                    {
                        finalEnumTypeID = bda.Attribute("type")?.Value ?? finalEnumTypeID;
                    }
                }
                if (enumTypes.TryGetValue(finalEnumTypeID, out var enumType))
                {
                    foreach (var enumVal in enumType.Elements(ns + "EnumVal"))
                    {
                        parameter.Add(new XElement("EnumValue", enumVal.Value, new XAttribute("EnumId", enumVal.Attribute("ord")?.Value ?? "")));
                    }
                }
            }

            parameter.Add(new XElement("Value", ""));

            if ((fc == "SE" || fc == "SP") && (finalDataType == "Float" || finalDataType == "Integer"))
            {
                parameter.Add(new XElement("MinValue", ""));
                parameter.Add(new XElement("MaxValue", ""));
                parameter.Add(new XElement("StepSize", ""));
                if (ParameterRequiresUnit(baseName))
                {
                    parameter.Add(new XElement("Unit", ""));
                }
            }

            parentElement.Add(parameter);
        }

        /// <summary>
        /// İstenen SGCB parametrelerini manuel olarak oluşturur ve ilgili gruba ekler.
        /// </summary>
        private static void AddManualSGCBParameters(XElement settingsContainer, string fullLdName, string lnName)
        {
            var sgcBParameters = new[]
            {
                new { Name = "ActSG",   DataType = "Integer", ObjectAddress = $"{fullLdName}/{lnName}.SGCB.ActSG" },
                new { Name = "CnfEdit", DataType = "Boolean", ObjectAddress = $"{fullLdName}/{lnName}.SGCB.CnfEdit" },
                new { Name = "EditSG",  DataType = "Integer", ObjectAddress = $"{fullLdName}/{lnName}.SGCB.EditSG" },
                new { Name = "LActTm",  DataType = "String",  ObjectAddress = $"{fullLdName}/{lnName}.SGCB.LActTm" }, // UTC_TIME -> String
                new { Name = "NumOfSG", DataType = "Integer", ObjectAddress = $"{fullLdName}/{lnName}.SGCB.NumOfSG" },
                new { Name = "ResvTms", DataType = "Integer", ObjectAddress = $"{fullLdName}/{lnName}.SGCB.ResvTms" }
            };

            foreach (var p in sgcBParameters)
            {
                var parameter = new XElement("Parameter",
                    new XElement("Name", FriendlyNames[p.Name]),
                    new XElement("ObjectAddress", p.ObjectAddress),
                    new XElement("FunctionalConstraint", "SP"),
                    new XElement("DataType", p.DataType),
                    new XElement("Value", ""),
                    new XElement("MinValue", ""),
                    new XElement("MaxValue", ""),
                    new XElement("StepSize", ""),
                    new XElement("Unit", "")
                );
                settingsContainer.Add(parameter);
            }
        }

        private static bool ParameterRequiresUnit(string baseName)
        {
            var unitlessParameters = new HashSet<string>
            {
                "TmMult", "StrValMul", "OpCnt", "FltNum", "DfdtCyclesNb", "DfdtValidNb"
            };
            return !unitlessParameters.Contains(baseName);
        }

        private static string DetermineCorrectDataType(string fc, string daName, string bType, string objectAddress)
        {
            if (fc == "CO")
            {
                if (daName == "Oper")
                {
                    if (objectAddress.EndsWith("ActSG.Oper")) return "Integer";
                    if (objectAddress.Contains(".Mod.Oper")) return "Enumeration";
                    return "Boolean";
                }
                return GetFriendlyDataType(bType);
            }
            if ((fc == "SE" || fc == "SP") && daName == "setMag") return "Float";
            if ((fc == "MX" || fc == "ST") && (daName == "mag" || daName == "f" || daName == "instMag")) return "Float";
            
            return GetFriendlyDataType(bType);
        }

        private static void PruneEmptyGroups(XElement element)
        {
            foreach (var child in element.Elements("Group").ToList())
            {
                PruneEmptyGroups(child);
            }
            if (element.Name == "Group" && !element.HasElements)
            {
                element.Remove();
            }
        }

        private static string GetFriendlyDataType(string bType)
        {
            if (string.IsNullOrEmpty(bType)) return "String";
            switch (bType.ToUpper())
            {
                case "FLOAT32": case "FLOAT64": return "Float";
                case "INT8":
                case "INT16":
                case "INT32":
                case "INT64":
                case "INT8U":
                case "INT16U":
                case "INT32U": return "Integer";
                case "BOOLEAN": return "Boolean";
                case "ENUM": case "ENUMERATED": return "Enumeration";
                case "STRUCT": return "String"; 
                default: return "String";
            }
        }

        private static XElement FindOrCreateGroup(XElement root, string[] path)
        {
            XElement current = root;
            foreach (var groupName in path)
            {
                XElement? next = current.Elements("Group").FirstOrDefault(g => g.Attribute("Name")?.Value == groupName);
                if (next == null)
                {
                    next = new XElement("Group", new XAttribute("Name", groupName));
                    current.Add(next);
                }
                current = next;
            }
            return current;
        }
    }

    class Dema
    {
        static void Main(string[] args)
        {
            string inputFile = "DPM400D_08_IED1.xml";
            string outputFile = "DPM400D_Output.xml";

            try
            {
                if (!File.Exists(inputFile))
                {
                    Console.WriteLine($"Hata: '{inputFile}' dosyası bulunamadı. Lütfen dosyanın programla aynı dizinde olduğundan emin olun.");
                    return;
                }
                string dpmSclContent = File.ReadAllText(inputFile);
                XDocument hierarchicalXmlDoc = DemaProject.ConvertDpmToHierarchical(dpmSclContent);
                hierarchicalXmlDoc.Save(outputFile);

                Console.WriteLine($"\nBaşarılı! Çıktı dosyası: {Path.GetFullPath(outputFile)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nBeklenmedik bir hata oluştu: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}