﻿using EFCorePowerTools.Shared.Annotations;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Scaffolding;
using RevEng.Core.Procedures.Model;
using RevEng.Core.Procedures.Model.Metadata;
using RevEng.Core.Procedures.Scaffolding;
using ReverseEngineer20;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace RevEng.Core.Procedures
{
    public class SqlServerStoredProcedureScaffolder : IProcedureScaffolder
    {
        private readonly IProcedureModelFactory procedureModelFactory;
        private readonly ICSharpHelper code;

        private static readonly ISet<SqlDbType> _scaleTypes = new HashSet<SqlDbType>
        {
            SqlDbType.DateTimeOffset,
            SqlDbType.DateTime2,
            SqlDbType.Time,
            SqlDbType.Decimal,
        };

        private static readonly ISet<SqlDbType> _lengthRequiredTypes = new HashSet<SqlDbType>
        {
            SqlDbType.Binary,
            SqlDbType.VarBinary,
            SqlDbType.Char,
            SqlDbType.VarChar,
            SqlDbType.NChar,
            SqlDbType.NVarChar,
        };

        private IndentedStringBuilder _sb;

        public SqlServerStoredProcedureScaffolder(IProcedureModelFactory procedureModelFactory, [NotNull] ICSharpHelper code)
        {
            this.procedureModelFactory = procedureModelFactory;
            this.code = code;
        }

        public ScaffoldedModel ScaffoldModel(string connectionString, ProcedureScaffolderOptions procedureScaffolderOptions, ProcedureModelFactoryOptions procedureModelFactoryOptions, ref List<string> errors)
        {
            var result = new ScaffoldedModel();

            var model = procedureModelFactory.Create(connectionString, procedureModelFactoryOptions);

            errors = model.Errors;

            foreach (var procedure in model.Procedures)
            {
                var name = GenerateIdentifierName(procedure, model) + "Result";

                var classContent = WriteResultClass(procedure, procedureScaffolderOptions.ModelNamespace, name);

                result.AdditionalFiles.Add(new ScaffoldedFile
                {
                    Code = classContent,
                    Path = $"{name}.cs",
                });
            }

            var dbContext = WriteProcedureDbContext(procedureScaffolderOptions, model);

            result.ContextFile = new ScaffoldedFile
            {
                Code = dbContext,
                Path = Path.GetFullPath(Path.Combine(procedureScaffolderOptions.ContextDir, procedureScaffolderOptions.ContextName + "Procedures.cs")),
            };

            return result;
        }

        public SavedModelFiles Save(ScaffoldedModel scaffoldedModel, string outputDir, string nameSpace)
        {
            Directory.CreateDirectory(outputDir);

            var contextPath = Path.GetFullPath(Path.Combine(outputDir, scaffoldedModel.ContextFile.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(contextPath));
            File.WriteAllText(contextPath, scaffoldedModel.ContextFile.Code, Encoding.UTF8);

            var additionalFiles = new List<string>();

            var dbContextExtensionsText = GetDbContextExtensionsText();
            var dbContextExtensionsPath = Path.Combine(Path.GetDirectoryName(contextPath), "DbContextExtensions.cs");
            File.WriteAllText(dbContextExtensionsPath, dbContextExtensionsText.Replace("#NAMESPACE#", nameSpace), Encoding.UTF8);
            additionalFiles.Add(dbContextExtensionsPath);

            foreach (var entityTypeFile in scaffoldedModel.AdditionalFiles)
            {
                var additionalFilePath = Path.Combine(outputDir, entityTypeFile.Path);
                File.WriteAllText(additionalFilePath, entityTypeFile.Code, Encoding.UTF8);
                additionalFiles.Add(additionalFilePath);
            }

            return new SavedModelFiles(contextPath, additionalFiles);
        }

        private string GetDbContextExtensionsText()
        {
            var assembly = typeof(SqlServerStoredProcedureScaffolder).GetTypeInfo().Assembly;
            using Stream stream = assembly.GetManifestResourceStream("RevEng.Core.DbContextExtensions");
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private string WriteProcedureDbContext(ProcedureScaffolderOptions procedureScaffolderOptions, ProcedureModel model)
        {
            _sb = new IndentedStringBuilder();

            _sb.AppendLine(PathHelper.Header);

            _sb.AppendLine("using Microsoft.EntityFrameworkCore;");
            _sb.AppendLine("using Microsoft.Data.SqlClient;");
            _sb.AppendLine("using System;");
            _sb.AppendLine("using System.Data;");
            _sb.AppendLine("using System.Threading.Tasks;");
            _sb.AppendLine($"using {procedureScaffolderOptions.ModelNamespace};");

            _sb.AppendLine();
            _sb.AppendLine($"namespace {procedureScaffolderOptions.ContextNamespace}");
            _sb.AppendLine("{");

            using (_sb.Indent())
            {
                _sb.AppendLine($"public partial class {procedureScaffolderOptions.ContextName}Procedures");

                _sb.AppendLine("{");

                using (_sb.Indent())
                {
                    _sb.AppendLine($"private readonly {procedureScaffolderOptions.ContextName} _context;");
                    _sb.AppendLine();
                    _sb.AppendLine($"public {procedureScaffolderOptions.ContextName}Procedures({procedureScaffolderOptions.ContextName} context)");
                    _sb.AppendLine("{");

                    using (_sb.Indent())
                    {
                        _sb.AppendLine($"_context = context;");
                    }

                    _sb.AppendLine("}");
                }

                foreach (var procedure in model.Procedures)
                {
                    GenerateProcedure(procedure, model);
                }

                _sb.AppendLine("}");
            }

            _sb.AppendLine("}");

            return _sb.ToString();
        }

        private void GenerateProcedure(Procedure procedure, ProcedureModel model)
        {
            var paramStrings = procedure.Parameters.Where(p => !p.Output)
                .Select(p => $"{code.Reference(p.ClrType())} {p.Name}");

            var outParams = procedure.Parameters.Where(p => p.Output).ToList();

            var retValueName = outParams.Last().Name;

            outParams.RemoveAt(outParams.Count - 1);

            var outParamStrings = outParams
                .Select(p => $"OutputParameter<{code.Reference(p.ClrType())}> {p.Name}")
                .ToList();

            string fullExec = GenerateProcedureStatement(procedure, retValueName);

            var identifier = GenerateIdentifierName(procedure, model);

            var line = GenerateMethodSignature(procedure, outParams, paramStrings, retValueName, outParamStrings, identifier);

            using (_sb.Indent())
            {
                _sb.AppendLine();

                _sb.AppendLine($"{line})");
                _sb.AppendLine("{");

                using (_sb.Indent())
                {
                    foreach (var parameter in procedure.Parameters)
                    {
                        GenerateParameters(parameter);
                    }

                    if (procedure.ResultElements.Count == 0)
                    {
                        _sb.AppendLine($"var _ = await _context.Database.ExecuteSqlRawAsync({fullExec});");
                    }
                    else
                    {
                        _sb.AppendLine($"var _ = await _context.SqlQuery<{identifier}Result>({fullExec});");
                    }

                    _sb.AppendLine();

                    foreach (var parameter in outParams)
                    {
                        _sb.AppendLine($"{parameter.Name}.SetValue(parameter{parameter.Name}.Value);");
                    }

                    _sb.AppendLine($"{retValueName}?.SetValue(parameter{retValueName}.Value);");

                    _sb.AppendLine();

                    _sb.AppendLine("return _;");
                }

                _sb.AppendLine("}");
            }
        }

        private static string GenerateProcedureStatement(Procedure procedure, string retValueName)
        {
            var paramNames = procedure.Parameters
                .Select(p => $"parameter{p.Name}");

            var paramList = procedure.Parameters
                .Select(p => p.Output ? $"@{p.Name} OUTPUT" : $"@{p.Name}").ToList();

            paramList.RemoveAt(paramList.Count - 1);

            var fullExec = $"\"EXEC @{retValueName} = [{procedure.Schema}].[{procedure.Name}] {string.Join(", ", paramList)}\", {string.Join(", ", paramNames)}".Replace(" \"", "\"");
            return fullExec;
        }

        private static string GenerateMethodSignature(Procedure procedure, List<ProcedureParameter> outParams, IEnumerable<string> paramStrings, string retValueName, List<string> outParamStrings, string identifier)
        {
            string line;

            if (procedure.ResultElements.Count == 0)
            {
                line = $"public async Task<int> {identifier}({string.Join(", ", paramStrings)}";
            }
            else
            {
                line = $"public async Task<{identifier}Result[]> {identifier}({string.Join(", ", paramStrings)}";
            }

            if (outParams.Count() > 0)
            {
                if (paramStrings.Count() > 0)
                {
                    line += ", ";
                }

                line += $"{string.Join(", ", outParamStrings)}";
            }

            if (paramStrings.Count() > 0 || outParams.Count > 0)
            {
                line += ", ";
            }

            line += $"OutputParameter<int> {retValueName} = null";
            
            return line;
        }

        private void GenerateParameters(ProcedureParameter parameter)
        {
            _sb.AppendLine($"var parameter{parameter.Name} = new SqlParameter");
            _sb.AppendLine("{");

            var sqlDbType = parameter.DbType();

            using (_sb.Indent())
            {
                _sb.AppendLine($"ParameterName = \"{parameter.Name}\",");

                if (_scaleTypes.Contains(sqlDbType))
                {
                    if (parameter.Precision > 0)
                    {
                        _sb.AppendLine($"Precision = {parameter.Precision},");
                    }
                    if (parameter.Scale > 0)
                    {
                        _sb.AppendLine($"Scale = {parameter.Scale},");
                    }
                }

                if (parameter.Length > 0 && _lengthRequiredTypes.Contains(sqlDbType))
                {
                    _sb.AppendLine($"Size = {parameter.Length},");
                }

                if (parameter.Output)
                {
                    _sb.AppendLine("Direction = System.Data.ParameterDirection.Output,");
                }
                else
                {
                    var value = parameter.Nullable ? $"{parameter.Name} ?? Convert.DBNull" : $"{parameter.Name}";
                    _sb.AppendLine($"Value = {value},");
                }

                _sb.AppendLine($"SqlDbType = System.Data.SqlDbType.{sqlDbType},");

                if (sqlDbType == System.Data.SqlDbType.Structured)
                {
                    _sb.AppendLine($"TypeName = \"{parameter.TypeName}\",");
                }
            }

            _sb.AppendLine("};");
            _sb.AppendLine();
        }

        private string WriteResultClass(Procedure storedProcedure, string @namespace, string name)
        {
            _sb = new IndentedStringBuilder();

            _sb.AppendLine(PathHelper.Header);
            _sb.AppendLine("using System;");
            _sb.AppendLine("using System.Collections.Generic;");
            _sb.AppendLine("using System.ComponentModel.DataAnnotations.Schema;");
            
            _sb.AppendLine();
            _sb.AppendLine($"namespace {@namespace}");
            _sb.AppendLine("{");

            using (_sb.Indent())
            {
                GenerateClass(storedProcedure, name);
            }

            _sb.AppendLine("}");

            return _sb.ToString();
        }

        private void GenerateClass(Procedure storedProcedure, string name)
        {
            _sb.AppendLine($"public partial class {name}");
            _sb.AppendLine("{");

            using (_sb.Indent())
            {
                GenerateProperties(storedProcedure);
            }

            _sb.AppendLine("}");
        }

        private void GenerateProperties(Procedure storedProcedure)
        {
            foreach (var property in storedProcedure.ResultElements.OrderBy(e => e.Ordinal))
            {
                var propertyNames = GeneratePropertyName(property.Name);

                if (!string.IsNullOrEmpty(propertyNames.Item2))
                {
                    _sb.AppendLine(propertyNames.Item2);
                }

                _sb.AppendLine($"public {code.Reference(property.ClrType())} {propertyNames.Item1} {{ get; set; }}");
            }
        }

        private Tuple<string, string> GeneratePropertyName(string propertyName)
        {
            if (propertyName == null)
            {
                throw new ArgumentNullException(nameof(propertyName));
            }

            return CreateIdentifier(propertyName);
        }

        private string GenerateIdentifierName(Procedure procedure, ProcedureModel model)
        {
            if (procedure == null)
            {
                throw new ArgumentNullException(nameof(procedure));
            }

            return CreateIdentifier(GenerateUniqueName(procedure, model)).Item1;
        }

        private Tuple<string, string> CreateIdentifier(string name)
        {
            var isValid = System.CodeDom.Compiler.CodeGenerator.IsValidLanguageIndependentIdentifier(name);

            string columAttribute = null;

            if (!isValid)
            {
                columAttribute = $"[Column(\"{name}\")]";
                // File name contains invalid chars, remove them
                var regex = new Regex(@"[^\p{Ll}\p{Lu}\p{Lt}\p{Lo}\p{Nd}\p{Nl}\p{Mn}\p{Mc}\p{Cf}\p{Pc}\p{Lm}]", RegexOptions.None, TimeSpan.FromSeconds(5));
                name = regex.Replace(name, "");

                // Class name doesn't begin with a letter, insert an underscore
                if (!char.IsLetter(name, 0))
                {
                    name = name.Insert(0, "_");
                }
            }

            return new Tuple<string, string>(name.Replace(" ", string.Empty), columAttribute);
        }

        private string GenerateUniqueName(Procedure procedure, ProcedureModel model)
        {
            var numberOfNames = model.Procedures.Where(p => p.Name == procedure.Name).Count();

            if (numberOfNames > 1)
            {
                return procedure.Name + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(procedure.Schema);
            }

            return procedure.Name;
        }
    }
}
