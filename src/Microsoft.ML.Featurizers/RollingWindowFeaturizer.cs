// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;
using Microsoft.ML;
using Microsoft.ML.CommandLine;
using Microsoft.ML.Data;
using Microsoft.ML.EntryPoints;
using Microsoft.ML.Featurizers;
using Microsoft.ML.Internal.Utilities;
using Microsoft.ML.Model.OnnxConverter;
using Microsoft.ML.Runtime;
using Microsoft.ML.Transforms;
using Microsoft.Win32.SafeHandles;
using static Microsoft.ML.Featurizers.CommonExtensions;
using static Microsoft.ML.SchemaShape.Column;

[assembly: LoadableClass(typeof(RollingWindowTransformer), null, typeof(SignatureLoadModel),
    RollingWindowTransformer.UserName, RollingWindowTransformer.LoaderSignature)]

[assembly: LoadableClass(typeof(IDataTransform), typeof(RollingWindowTransformer), null, typeof(SignatureLoadDataTransform),
    RollingWindowTransformer.UserName, RollingWindowTransformer.LoaderSignature)]

[assembly: EntryPointModule(typeof(RollingWindowEntrypoint))]

namespace Microsoft.ML.Featurizers
{
    public static class RollingWindowExtensionClass
    {
        // TODO: review naming for public api before going into master.

        /// <summary>
        /// Creates a <see cref="RollingWindowEstimator"/> which computes rolling window calculations per grain. The currently supported window calculations are
        /// mean, min, and max. This also adds annotations to the output column to track the min/max window sizes, as well as what calculation was performed. The horizon
        /// is an initial offset, and the window calculation is performed starting at that initial offset, and then looping -1 until that offset is equal to 1.
        /// </summary>
        /// <param name="catalog">Transform catalog</param>
        /// <param name="grainColumns">The list of grain columns</param>
        /// <param name="outputColumn">Where to store the result of the calculation</param>
        /// <param name="windowCalculation">The window calculation to perform</param>
        /// <param name="horizon">The Horizon represents the maximum value in a range [1, N], where each element in that range is a delta applied to the start of the window.</param>
        /// <param name="maxWindowSize">The maximum number of items in the window</param>
        /// <param name="minWindowSize">The minimum number of items required. If there are less, double.NaN is returned.</param>
        /// <param name="inputColumn">The source column.</param>
        /// <returns></returns>
        public static RollingWindowEstimator RollingWindow(this TransformsCatalog catalog, string[] grainColumns, string outputColumn, RollingWindowEstimator.RollingWindowCalculation windowCalculation,
            UInt32 horizon, UInt32 maxWindowSize, UInt32 minWindowSize = 1, string inputColumn = null)
        {
            var options = new RollingWindowEstimator.Options
            {
                GrainColumns = grainColumns,
                Column = new[] { new RollingWindowEstimator.Column() { Name = outputColumn, Source = inputColumn ?? outputColumn } },
                Horizon = horizon,
                MaxWindowSize = maxWindowSize,
                MinWindowSize = minWindowSize,
                WindowCalculation = windowCalculation
            };

            return new RollingWindowEstimator(CatalogUtils.GetEnvironment(catalog), options);
        }

        /// <summary>
        /// Creates a <see cref="RollingWindowEstimator"/> which computes rolling window calculations per grain. The currently supported window calculations are
        /// mean, min, and max. This also adds annotations to the output column to track the min/max window sizes, as well as what calculation was performed. The horizon
        /// is an initial offset, and the window calculation is performed starting at that initial offset, and then looping -1 until that offset is equal to 1.
        /// </summary>
        /// <param name="catalog">Transform catalog</param>
        /// <param name="grainColumns">The list of grain columns</param>
        /// <param name="columns">List of columns mappings</param>
        /// <param name="windowCalculation">The window calculation to perform</param>
        /// <param name="horizon">The Horizon represents the maximum value in a range [1, N], where each element in that range is a delta applied to the start of the window.</param>
        /// <param name="maxWindowSize">The maximum number of items in the window</param>
        /// <param name="minWindowSize">The minimum number of items required. If there are less, double.NaN is returned.</param>
        /// <returns></returns>
        public static RollingWindowEstimator RollingWindow(this TransformsCatalog catalog, string[] grainColumns, InputOutputColumnPair[] columns, RollingWindowEstimator.RollingWindowCalculation windowCalculation,
            UInt32 horizon, UInt32 maxWindowSize, UInt32 minWindowSize = 1)
        {
            var options = new RollingWindowEstimator.Options
            {
                GrainColumns = grainColumns,
                Column = columns.Select(x => new RollingWindowEstimator.Column { Name = x.OutputColumnName, Source = x.InputColumnName ?? x.OutputColumnName }).ToArray(),
                Horizon = horizon,
                MaxWindowSize = maxWindowSize,
                MinWindowSize = minWindowSize,
                WindowCalculation = windowCalculation
            };

            return new RollingWindowEstimator(CatalogUtils.GetEnvironment(catalog), options);
        }
    }

    /// <summary>
    /// RollingWindow featurizer performs a rolling calculation over a window of data per grain. The currently supported window calculations are
    /// mean, min, and max. This also adds annotations to the output column to track the min/max window sizes, as well as what calculation was performed. The horizon
    /// is an initial offset, and the window calculation is performed starting at that initial offset, and then looping -1 until that offset is equal to 1.
    /// </summary>
    /// <remarks>
    /// <format type="text/markdown"><![CDATA[
    ///
    /// ###  Estimator Characteristics
    /// |  |  |
    /// | -- | -- |
    /// | Does this estimator need to look at the data to train its parameters? | No |
    /// | Input column data type | double |
    /// | Output column data type | vector of double. Size of vector is equal to the horizon |
    ///
    /// The <xref:Microsoft.ML.Transforms.RollingWindowEstimator> is a trivial estimator and doesn't need training.
    /// A simple example would be horizon = 1, maxWindowSize = 2, and we want to take the minimum.
    ///
    ///      +-----------+-------+-------------------+
    ///      | grain     | target| target_minimum    |
    ///      +===========+=======+===================+
    ///      | A         | 10    | [[NAN]]           |
    ///      +-----------+-------+-------------------+
    ///      | A         | 4     | [[10]]            |
    ///      +-----------+-------+-------------------+
    ///      | A         | 6     | [[4]]             |
    ///      +-----------+-------+-------------------+
    ///      | A         | 11    | [[4]]             |
    ///      +-----------+-------+-------------------+
    ///
    ///      A more complex example would be, assuming we have horizon = 2, maxWindowSize = 2, minWindowSize = 2, and we want the maximum value
    ///      +-----------+-------+-------------------+
    ///      | grain     | target| target_max        |
    ///      +===========+=======+===================+
    ///      | A         | 10    | [[NAN, NAN]]      |
    ///      +-----------+-------+-------------------+
    ///      | A         | 4     | [[NAN, NAN]]      |
    ///      +-----------+-------+-------------------+
    ///      | A         | 6     | [[NAN, 10]]       |
    ///      +-----------+-------+-------------------+
    ///      | A         | 11    | [[10, 6]]         |
    ///      +-----------+-------+-------------------+
    ///
    /// ]]>
    /// </format>
    /// </remarks>
    /// <seealso cref="RollingWindowExtensionClass.RollingWindow(TransformsCatalog, string[], string, RollingWindowEstimator.RollingWindowCalculation, UInt32, UInt32, UInt32, string)"/>
    /// <seealso cref="RollingWindowExtensionClass.RollingWindow(TransformsCatalog, string[], InputOutputColumnPair[], RollingWindowEstimator.RollingWindowCalculation, UInt32, UInt32, UInt32)"/>
    public class RollingWindowEstimator : IEstimator<RollingWindowTransformer>
    {
        private Options _options;
        private readonly IHost _host;

        #region Options

        internal sealed class Column : OneToOneColumn
        {
            internal static Column Parse(string str)
            {
                Contracts.AssertNonEmpty(str);

                var res = new Column();
                if (res.TryParse(str))
                    return res;
                return null;
            }

            internal bool TryUnparse(StringBuilder sb)
            {
                Contracts.AssertValue(sb);
                return TryUnparseCore(sb);
            }
        }

        internal sealed class Options : TransformInputBase
        {
            [Argument((ArgumentType.MultipleUnique | ArgumentType.Required), HelpText = "List of grain columns",
                Name = "GrainColumn", ShortName = "grains", SortOrder = 0)]
            public string[] GrainColumns;

            [Argument(ArgumentType.Multiple | ArgumentType.Required, HelpText = "New column definition (optional form: name:src)",
                Name = "Column", ShortName = "col", SortOrder = 1)]
            public Column[] Column;

            [Argument(ArgumentType.AtMostOnce | ArgumentType.Required, HelpText = "Maximum horizon value",
                Name = "Horizon", ShortName = "hor", SortOrder = 2)]
            public UInt32 Horizon;

            [Argument(ArgumentType.AtMostOnce | ArgumentType.Required, HelpText = "Maximum window size",
                Name = "MaxWindowSize", ShortName = "maxsize", SortOrder = 3)]
            public UInt32 MaxWindowSize;

            [Argument(ArgumentType.AtMostOnce | ArgumentType.Required, HelpText = "Minimum window size",
                Name = "MinWindowSize", ShortName = "minsize", SortOrder = 4)]
            public UInt32 MinWindowSize = 1;

            [Argument(ArgumentType.AtMostOnce | ArgumentType.Required, HelpText = "What window calculation to use",
                Name = "WindowCalculation", ShortName = "calc", SortOrder = 5)]
            public RollingWindowCalculation WindowCalculation;
        }

        #endregion

        #region Class Enums

        /// <summary>
        /// This is a representation of which RollingWindowCalculation to perform.
        /// Mean is the arithmatic mean of the window.
        /// Min is the minimum value in the window.
        /// Max is the maximum value in the window.
        /// </summary>
        public enum RollingWindowCalculation : byte
        {
            /// <summary>
            /// Mean is the arithmatic mean of the window.
            /// </summary>
            Mean = 1,

            /// <summary>
            /// Min is the minimum value in the window.
            /// </summary>
            Min = 2,

            /// <summary>
            /// Max is the maximum value in the window.
            /// </summary>
            Max = 3
        };

        #endregion

        internal RollingWindowEstimator(IHostEnvironment env, Options options)
        {
            Contracts.CheckValue(env, nameof(env));
            _host.Check(!CommonExtensions.OsIsCentOS7(), "CentOS7 is not supported");

            _host = env.Register(nameof(RollingWindowEstimator));
            _host.CheckValue(options.GrainColumns, nameof(options.GrainColumns), "Grain columns should not be null.");
            _host.CheckNonEmpty(options.GrainColumns, nameof(options.GrainColumns), "Need at least one grain column.");
            _host.CheckValue(options.Column, nameof(options.Column), "Columns should not be null.");
            _host.CheckNonEmpty(options.Column, nameof(options.Column), "Need at least one column pair.");
            _host.Check(options.Horizon > 0, "Can't have a horizon of 0.");
            _host.Check(options.MinWindowSize > 0, "Min window size must be greater then 0.");
            _host.Check(options.MaxWindowSize > 0, "Max window size must be greater then 0.");
            _host.Check(options.MaxWindowSize >= options.MinWindowSize, "Max window size must be greater or equal to min window size.");
            _host.Check(options.Horizon <= int.MaxValue, "Horizon must be less then or equal to int.max");

            _options = options;
        }

        public RollingWindowTransformer Fit(IDataView input)
        {
            return new RollingWindowTransformer(_host, input, _options);
        }

        public SchemaShape GetOutputSchema(SchemaShape inputSchema)
        {
            var columns = inputSchema.ToDictionary(x => x.Name);

            // These are used in generating the column name, but don't need to be recreated in the loop.
            var calculationName = Enum.GetName(typeof(RollingWindowEstimator.RollingWindowCalculation), _options.WindowCalculation);
            var minWinName = $"MinWin{_options.MinWindowSize}";
            var maxWinName = $"MaxWin{_options.MaxWindowSize}";

            foreach (var column in _options.Column)
            {

                var inputColumn = columns[column.Source];

                if (!RollingWindowTransformer.TypedColumn.IsColumnTypeSupported(inputColumn.ItemType.RawType))
                    throw new InvalidOperationException($"Type {inputColumn.ItemType.RawType} for column {column.Source} not a supported type.");

                // Create annotations
                // Since we can't get the value of the annotation from the schema shape, the current workaround is naming annotation with the value as well.
                // This workaround will need to be removed when the limitation is resolved.
                var sourceColName = column.Name;
                var annotations = new DataViewSchema.Annotations.Builder();

                ValueGetter<ReadOnlyMemory<char>> nameValueGetter = (ref ReadOnlyMemory<char> dst) => dst = $"{sourceColName}_{calculationName}_{minWinName}_{maxWinName}".AsMemory();

                annotations.Add<ReadOnlyMemory<char>>($"ColumnNames={sourceColName}_{calculationName}_{minWinName}_{maxWinName}", TextDataViewType.Instance, nameValueGetter);

                columns[column.Name] = new SchemaShape.Column(column.Name, VectorKind.Vector,
                    NumberDataViewType.Double, false, SchemaShape.Create(annotations.ToAnnotations().Schema));
            }

            return new SchemaShape(columns.Values);
        }
    }

    public sealed class RollingWindowTransformer : ITransformer, IDisposable
    {
        #region Class data members

        internal const string Summary = "Performs a calculation over a rolling timeseries window";
        internal const string UserName = "Rolling Window Featurizer";
        internal const string ShortName = "RollingWindow";
        internal const string LoaderSignature = "RollingWindow";

        private TypedColumn[] _columns;
        private readonly IHost _host;
        private RollingWindowEstimator.Options _options;

        #endregion

        internal RollingWindowTransformer(IHostEnvironment host, IDataView input, RollingWindowEstimator.Options options)
        {
            _host = host.Register(nameof(RollingWindowTransformer));
            var schema = input.Schema;
            _options = options;

            _columns = options.Column.Select(x => TypedColumn.CreateTypedColumn(x.Name, x.Source, schema[x.Source].Type.RawType.ToString(), _options)).ToArray();
            foreach (var column in _columns)
            {
                column.CreateTransformerFromEstimator(input);
            }
        }

        // Factory method for SignatureLoadModel.
        internal RollingWindowTransformer(IHostEnvironment host, ModelLoadContext ctx)
        {
            _host = host.Register(nameof(RollingWindowTransformer));
            _host.CheckValue(ctx, nameof(ctx));
            _host.Check(!CommonExtensions.OsIsCentOS7(), "CentOS7 is not supported");

            ctx.CheckAtModel(GetVersionInfo());

            // *** Binary format ***
            // int length of grainColumns
            // string[] grainColumns
            // uint32 horizon
            // uint32 maxWindowSize
            // uint32 minWindowSize
            // byte windowCalculation
            // int number of column pairs
            // for each column pair:
            //      string output column name
            //      string source column name
            //      column type
            //      int length of c++ byte array
            //      byte array from c++

            var grainColumns = new string[ctx.Reader.ReadInt32()];
            for (int i = 0; i < grainColumns.Length; i++)
            {
                grainColumns[i] = ctx.Reader.ReadString();
            }

            var horizon = ctx.Reader.ReadUInt32();
            var maxWindowSize = ctx.Reader.ReadUInt32();
            var minWindowSize = ctx.Reader.ReadUInt32();
            var windowCalculation = ctx.Reader.ReadByte();

            var columnCount = ctx.Reader.ReadInt32();
            _columns = new TypedColumn[columnCount];

            _options = new RollingWindowEstimator.Options()
            {
                GrainColumns = grainColumns,
                Column = new RollingWindowEstimator.Column[columnCount],
                Horizon = horizon,
                MaxWindowSize = maxWindowSize,
                MinWindowSize = minWindowSize,
                WindowCalculation = (RollingWindowEstimator.RollingWindowCalculation)windowCalculation
            };

            for (int i = 0; i < columnCount; i++)
            {
                var colName = ctx.Reader.ReadString();
                var sourceName = ctx.Reader.ReadString();
                _options.Column[i] = new RollingWindowEstimator.Column()
                {
                    Name = colName,
                    Source = sourceName
                };

                _columns[i] = TypedColumn.CreateTypedColumn(colName, sourceName, ctx.Reader.ReadString(), _options);

                // Load the C++ state and create the C++ transformer.
                var dataLength = ctx.Reader.ReadInt32();
                var data = ctx.Reader.ReadByteArray(dataLength);
                _columns[i].CreateTransformerFromSavedData(data);
            }
        }

        // Factory method for SignatureLoadDataTransform.
        private static IDataTransform Create(IHostEnvironment env, ModelLoadContext ctx, IDataView input)
        {
            return (IDataTransform)(new RollingWindowTransformer(env, ctx).Transform(input));
        }

        public DataViewSchema GetOutputSchema(DataViewSchema inputSchema)
        {
            // To add future support for when this will do multiple window sizes at once, output will be a 2d vector so nothing will need to change when that is implemented.

            // Create annotations
            // We create 4 annotations, these are used by the PivotFeaturizer.
            // We create annotations for the minWindowSize, maxWindowSize, this featurizerName, and which calculation was performed.
            // Since we can't get the value of the annotation from the schema shape, the current workaround is naming annotation with the value as well.
            // This workaround will need to be removed when the limitation is resolved.
            var schemaBuilder = new DataViewSchema.Builder();
            schemaBuilder.AddColumns(inputSchema.AsEnumerable());

            // These are used in generating the column name, but don't need to be recreated in the loop.
            var calculationName = Enum.GetName(typeof(RollingWindowEstimator.RollingWindowCalculation), _options.WindowCalculation);
            var minWinName = $"MinWin{_options.MinWindowSize}";
            var maxWinName = $"MaxWin{_options.MaxWindowSize}";

            for (int i = 0; i < _options.Column.Length; i++)
            {
                var sourceColName = _options.Column[i].Name;
                var annotations = new DataViewSchema.Annotations.Builder();

                ValueGetter<ReadOnlyMemory<char>> nameValueGetter = (ref ReadOnlyMemory<char> dst) => dst = $"{sourceColName}_{calculationName}_{minWinName}_{maxWinName}".AsMemory();

                annotations.Add<ReadOnlyMemory<char>>($"ColumnNames={sourceColName}_{calculationName}_{minWinName}_{maxWinName}", TextDataViewType.Instance, nameValueGetter);

                schemaBuilder.AddColumn(_options.Column[i].Name, new VectorDataViewType(NumberDataViewType.Double, 1, (int)_options.Horizon), annotations.ToAnnotations());
            }

            return schemaBuilder.ToSchema();
        }

        public bool IsRowToRowMapper => false;

        public IRowToRowMapper GetRowToRowMapper(DataViewSchema inputSchema) => throw new InvalidOperationException("Not a RowToRowMapper.");

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "ROLWIN T",
                verWrittenCur: 0x00010001,
                verReadableCur: 0x00010001,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature,
                loaderAssemblyName: typeof(RollingWindowTransformer).Assembly.FullName);
        }

        public void Save(ModelSaveContext ctx)
        {
            _host.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel();
            ctx.SetVersionInfo(GetVersionInfo());

            // *** Binary format ***
            // int length of grainColumns
            // string[] grainColumns
            // uint32 horizon
            // uint32 maxWindowSize
            // uint32 minWindowSize
            // byte windowCalculation
            // int number of column pairs
            // for each column pair:
            //      string output column name
            //      string source column name
            //      column type
            //      int length of c++ byte array
            //      byte array from c++

            ctx.Writer.Write(_options.GrainColumns.Length);
            foreach (var grain in _options.GrainColumns)
            {
                ctx.Writer.Write(grain);
            }
            ctx.Writer.Write(_options.Horizon);
            ctx.Writer.Write(_options.MaxWindowSize);
            ctx.Writer.Write(_options.MinWindowSize);
            ctx.Writer.Write((byte)_options.WindowCalculation);

            // Save interop data.
            ctx.Writer.Write(_columns.Count());
            foreach (var column in _columns)
            {
                ctx.Writer.Write(column.Name);
                ctx.Writer.Write(column.Source);
                ctx.Writer.Write(column.Type);

                // Save C++ state
                var data = column.CreateTransformerSaveData();
                ctx.Writer.Write(data.Length);
                ctx.Writer.Write(data);
            }
        }

        public IDataView Transform(IDataView input) => MakeDataTransform(input);

        internal RollingWindowDataView MakeDataTransform(IDataView input)
        {
            _host.CheckValue(input, nameof(input));

            return new RollingWindowDataView(_host, input, _options, this);
        }

        internal TransformerEstimatorSafeHandle[] CloneTransformers()
        {
            var transformers = new TransformerEstimatorSafeHandle[_columns.Length];
            for (int i = 0; i < _columns.Length; i++)
            {
                transformers[i] = _columns[i].CloneTransformer();
            }
            return transformers;
        }

        public void Dispose()
        {
            foreach (var column in _columns)
            {
                column.Dispose();
            }
        }

        #region Native Safe handle classes
        internal delegate bool DestroyTransformedVectorDataNative(IntPtr columns, IntPtr rows, IntPtr items, out IntPtr errorHandle);
        internal class TransformedVectorDataSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private readonly DestroyTransformedVectorDataNative _destroyTransformedDataHandler;
            private readonly IntPtr _columns;
            private readonly IntPtr _rows;

            public TransformedVectorDataSafeHandle(IntPtr handle, IntPtr columns, IntPtr rows, DestroyTransformedVectorDataNative destroyTransformedDataHandler) : base(true)
            {
                SetHandle(handle);
                _destroyTransformedDataHandler = destroyTransformedDataHandler;
                _columns = columns;
                _rows = rows;
            }

            protected override bool ReleaseHandle()
            {
                // Not sure what to do with error stuff here.  There shouldn't ever be one though.
                var success = _destroyTransformedDataHandler(_columns, _rows, handle, out IntPtr errorHandle);
                return success;
            }
        }

        #endregion

        #region ColumnInfo

        #region BaseClass
        // TODO: The majority of this base class can probably be moved into the common.cs file. Look more into this before merging into master.
        internal abstract class TypedColumn : IDisposable
        {
            internal readonly string Source;
            internal readonly string Type;
            internal readonly string Name;

            private protected TransformerEstimatorSafeHandle TransformerHandle;
            private static readonly Type[] _supportedTypes = new Type[] { typeof(double) };

            private protected string[] GrainColumns;

            internal TypedColumn(string name, string source, string type, string[] grainColumns, TransformerEstimatorSafeHandle transformer)
            {
                Source = source;
                Type = type;
                Name = name;
                GrainColumns = grainColumns;

                if (transformer != null)
                    TransformerHandle = transformer;
            }

            internal abstract void CreateTransformerFromEstimator(IDataView input);
            private protected abstract unsafe TransformerEstimatorSafeHandle CreateTransformerFromSavedDataHelper(byte* rawData, IntPtr dataSize);
            private protected abstract bool CreateTransformerSaveDataHelper(out IntPtr buffer, out IntPtr bufferSize, out IntPtr errorHandle);
            private protected abstract bool GetStateHelper(TransformerEstimatorSafeHandle estimator, out TrainingState trainingState, out IntPtr errorHandle);
            private protected abstract bool OnDataCompletedHelper(TransformerEstimatorSafeHandle estimator, out IntPtr errorHandle);
            public abstract void Dispose();

            public abstract Type ReturnType();
            public abstract Type SourceType();

            internal byte[] CreateTransformerSaveData()
            {

                var success = CreateTransformerSaveDataHelper(out IntPtr buffer, out IntPtr bufferSize, out IntPtr errorHandle);
                if (!success)
                    throw new Exception(GetErrorDetailsAndFreeNativeMemory(errorHandle));

                using (var savedDataHandle = new SaveDataSafeHandle(buffer, bufferSize))
                {
                    byte[] savedData = new byte[bufferSize.ToInt32()];
                    Marshal.Copy(buffer, savedData, 0, savedData.Length);
                    return savedData;
                }
            }

            internal unsafe void CreateTransformerFromSavedData(byte[] data)
            {
                fixed (byte* rawData = data)
                {
                    IntPtr dataSize = new IntPtr(data.Count());
                    TransformerHandle = CreateTransformerFromSavedDataHelper(rawData, dataSize);
                }
            }

            internal unsafe TransformerEstimatorSafeHandle CloneTransformer()
            {
                byte[] data = CreateTransformerSaveData();
                fixed (byte* rawData = data)
                {
                    IntPtr dataSize = new IntPtr(data.Count());
                    return CreateTransformerFromSavedDataHelper(rawData, dataSize);
                }
            }

            internal static bool IsColumnTypeSupported(Type type)
            {
                return _supportedTypes.Contains(type);
            }

            internal static TypedColumn CreateTypedColumn(string name, string source, string type, RollingWindowEstimator.Options options, TransformerEstimatorSafeHandle transformer = null)
            {
                if (type == typeof(double).ToString() && options.WindowCalculation == RollingWindowEstimator.RollingWindowCalculation.Mean)
                {
                    return new AnalyticalDoubleTypedColumn(name, source, options, transformer);
                }
                else if (type == typeof(double).ToString() && (options.WindowCalculation == RollingWindowEstimator.RollingWindowCalculation.Min || options.WindowCalculation == RollingWindowEstimator.RollingWindowCalculation.Max))
                {
                    return new SimpleDoubleTypedColumn(name, source, options, transformer);
                }

                throw new InvalidOperationException($"Column {source} has an unsupported type {type}.");
            }
        }

        internal abstract class TypedColumn<TSourceType, TOutputType> : TypedColumn
        {
            private protected DataViewRowCursor Cursor;
            private protected ValueGetter<ReadOnlyMemory<char>>[] GrainGetters;
            private protected readonly RollingWindowEstimator.Options Options;

            internal TypedColumn(string name, string source, string type, RollingWindowEstimator.Options options, TransformerEstimatorSafeHandle transformer) :
                base(name, source, type, options.GrainColumns, transformer)
            {
                Options = options;

                // Initialize to the correct length
                GrainGetters = new ValueGetter<ReadOnlyMemory<char>>[GrainColumns.Length];
                Cursor = null;
            }

            internal abstract TOutputType Transform(IntPtr grainsArray, IntPtr grainsArraySize, TSourceType input);
            private protected abstract bool CreateEstimatorHelper(out IntPtr estimator, out IntPtr errorHandle);
            private protected abstract bool CreateTransformerFromEstimatorHelper(TransformerEstimatorSafeHandle estimator, out IntPtr transformer, out IntPtr errorHandle);
            private protected abstract bool DestroyEstimatorHelper(IntPtr estimator, out IntPtr errorHandle);
            private protected abstract bool DestroyTransformerHelper(IntPtr transformer, out IntPtr errorHandle);
            private protected unsafe abstract bool FitHelper(TransformerEstimatorSafeHandle estimator, IntPtr grainsArray, IntPtr grainsArraySize, TSourceType value, out FitResult fitResult, out IntPtr errorHandle);
            private protected abstract bool CompleteTrainingHelper(TransformerEstimatorSafeHandle estimator, out IntPtr errorHandle);

            private protected TransformerEstimatorSafeHandle CreateTransformerFromEstimatorBase(IDataView input)
            {
                var success = CreateEstimatorHelper(out IntPtr estimator, out IntPtr errorHandle);
                if (!success)
                    throw new Exception(GetErrorDetailsAndFreeNativeMemory(errorHandle));

                using (var estimatorHandle = new TransformerEstimatorSafeHandle(estimator, DestroyEstimatorHelper))
                {
                    TrainingState trainingState;
                    FitResult fitResult;

                    // Declare these outside the loop so the size is only set once;
                    GCHandle[] grainHandles = new GCHandle[GrainColumns.Length];
                    IntPtr[] grainArray = new IntPtr[GrainColumns.Length];
                    GCHandle arrayHandle = default;

                    InitializeGrainGetters(input);

                    // Can't use a using with this because it potentially needs to be reset. Manually disposing as needed.
                    var data = input.GetColumn<TSourceType>(Source).GetEnumerator();
                    data.MoveNext();
                    while (true)
                    {
                        // Get the state of the native estimator.
                        success = GetStateHelper(estimatorHandle, out trainingState, out errorHandle);
                        if (!success)
                            throw new Exception(GetErrorDetailsAndFreeNativeMemory(errorHandle));

                        // If we are no longer training then exit loop.
                        if (trainingState != TrainingState.Training)
                            break;

                        // Build the grain string array
                        try
                        {
                            CreateGrainStringArrays(GrainGetters, ref grainHandles, ref arrayHandle, ref grainArray);

                            // Train the estimator
                            success = FitHelper(estimatorHandle, arrayHandle.AddrOfPinnedObject(), new IntPtr(grainArray.Length), data.Current, out fitResult, out errorHandle);
                            if (!success)
                                throw new Exception(GetErrorDetailsAndFreeNativeMemory(errorHandle));

                        }
                        finally
                        {
                            FreeGrainStringArrays(ref grainHandles, ref arrayHandle);
                        }

                        // If we need to reset the data to the beginning.
                        if (fitResult == FitResult.ResetAndContinue)
                        {
                            data.Dispose();
                            data = input.GetColumn<TSourceType>(Source).GetEnumerator();

                            InitializeGrainGetters(input);
                        }

                        // If we are at the end of the data.
                        if (!data.MoveNext() && !Cursor.MoveNext())
                        {
                            OnDataCompletedHelper(estimatorHandle, out errorHandle);
                            if (!success)
                                throw new Exception(GetErrorDetailsAndFreeNativeMemory(errorHandle));

                            // Re-initialize the data
                            data.Dispose();
                            data = input.GetColumn<TSourceType>(Source).GetEnumerator();
                            data.MoveNext();

                            InitializeGrainGetters(input);
                        }
                    }

                    // When done training complete the estimator.
                    success = CompleteTrainingHelper(estimatorHandle, out errorHandle);
                    if (!success)
                        throw new Exception(GetErrorDetailsAndFreeNativeMemory(errorHandle));

                    // Create the native transformer from the estimator;
                    success = CreateTransformerFromEstimatorHelper(estimatorHandle, out IntPtr transformer, out errorHandle);
                    if (!success)
                        throw new Exception(GetErrorDetailsAndFreeNativeMemory(errorHandle));

                    // Manually dispose of the IEnumerator and Cursor since we dont have a using statement;
                    data.Dispose();
                    Cursor.Dispose();

                    return new TransformerEstimatorSafeHandle(transformer, DestroyTransformerHelper);
                }
            }

            private bool InitializeGrainGetters(IDataView input)
            {
                // Create getters for the grain columns. Cant use using for the cursor because it may need to be reset.
                // Manually dispose of the cursor if its not null
                if (Cursor != null)
                    Cursor.Dispose();

                Cursor = input.GetRowCursor(input.Schema.Where(x => GrainColumns.Contains(x.Name)));

                for (int i = 0; i < GrainColumns.Length; i++)
                {
                    // Inititialize the enumerator and move it to a valid position.
                    if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(sbyte))
                        GrainGetters[i] = GetGrainGetter<sbyte>(GrainColumns[i]);
                    else if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(Int16))
                        GrainGetters[i] = GetGrainGetter<Int16>(GrainColumns[i]);
                    else if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(Int32))
                        GrainGetters[i] = GetGrainGetter<Int32>(GrainColumns[i]);
                    else if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(Int64))
                        GrainGetters[i] = GetGrainGetter<Int64>(GrainColumns[i]);
                    else if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(byte))
                        GrainGetters[i] = GetGrainGetter<byte>(GrainColumns[i]);
                    else if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(UInt16))
                        GrainGetters[i] = GetGrainGetter<UInt16>(GrainColumns[i]);
                    else if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(UInt32))
                        GrainGetters[i] = GetGrainGetter<UInt32>(GrainColumns[i]);
                    else if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(UInt64))
                        GrainGetters[i] = GetGrainGetter<UInt64>(GrainColumns[i]);
                    else if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(float))
                        GrainGetters[i] = GetGrainGetter<float>(GrainColumns[i]);
                    else if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(double))
                        GrainGetters[i] = GetGrainGetter<double>(GrainColumns[i]);
                    else if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(bool))
                        GrainGetters[i] = GetGrainGetter<bool>(GrainColumns[i]);
                    if (Cursor.Schema[GrainColumns[i]].Type.RawType == typeof(ReadOnlyMemory<char>))
                        GrainGetters[i] = Cursor.GetGetter<ReadOnlyMemory<char>>(Cursor.Schema[GrainColumns[i]]);
                }

                return Cursor.MoveNext();
            }

            private ValueGetter<ReadOnlyMemory<char>> GetGrainGetter<T>(string grainColumn)
            {
                var getter = Cursor.GetGetter<T>(Cursor.Schema[grainColumn]);
                T value = default;
                return (ref ReadOnlyMemory<char> dst) =>
                {
                    getter(ref value);
                    dst = value.ToString().AsMemory();
                };
            }

            public override Type ReturnType()
            {
                return typeof(TOutputType);
            }

            public override Type SourceType()
            {
                return typeof(TSourceType);
            }
        }

        #endregion

        // On the native side, these rolling windows are implemented as 2 separate featurizers.
        // We are only exposing 1 interface in ML.Net, but there needs to be interop code for both.
        #region AnalyticalDoubleTypedColumn

        internal sealed class AnalyticalDoubleTypedColumn : TypedColumn<double, VBuffer<double>>
        {
            internal AnalyticalDoubleTypedColumn(string name, string source, RollingWindowEstimator.Options options, TransformerEstimatorSafeHandle transformer) :
                base(name, source, typeof(double).ToString(), options, transformer)
            {
            }

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_CreateEstimator", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool CreateEstimatorNative(RollingWindowEstimator.RollingWindowCalculation windowCalculation, UInt32 horizon, UInt32 maxWindowSize, UInt32 minWindowSize, out IntPtr estimator, out IntPtr errorHandle);

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_DestroyEstimator", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool DestroyEstimatorNative(IntPtr estimator, out IntPtr errorHandle); // Should ONLY be called by safe handle

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_CreateTransformerFromEstimator", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool CreateTransformerFromEstimatorNative(TransformerEstimatorSafeHandle estimator, out IntPtr transformer, out IntPtr errorHandle);

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_DestroyTransformer", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool DestroyTransformerNative(IntPtr transformer, out IntPtr errorHandle);
            internal override void CreateTransformerFromEstimator(IDataView input)
            {
                TransformerHandle = CreateTransformerFromEstimatorBase(input);
            }

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_CreateTransformerFromSavedData", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static unsafe extern bool CreateTransformerFromSavedDataNative(byte* rawData, IntPtr bufferSize, out IntPtr transformer, out IntPtr errorHandle);
            private protected override unsafe TransformerEstimatorSafeHandle CreateTransformerFromSavedDataHelper(byte* rawData, IntPtr dataSize)
            {
                var result = CreateTransformerFromSavedDataNative(rawData, dataSize, out IntPtr transformer, out IntPtr errorHandle);
                if (!result)
                    throw new Exception(GetErrorDetailsAndFreeNativeMemory(errorHandle));

                return new TransformerEstimatorSafeHandle(transformer, DestroyTransformerNative);
            }

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_Transform", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static unsafe extern bool TransformDataNative(TransformerEstimatorSafeHandle transformer, IntPtr grainsArray, IntPtr grainsArraySize, double value, out IntPtr outputCols, out IntPtr outputRows, out double* output, out IntPtr errorHandle);
            internal unsafe override VBuffer<double> Transform(IntPtr grainsArray, IntPtr grainsArraySize, double input)
            {
                var success = TransformDataNative(TransformerHandle, grainsArray, grainsArraySize, input, out IntPtr outputCols, out IntPtr outputRows, out double* output, out IntPtr errorHandle);
                if (!success)
                    throw new Exception(GetErrorDetailsAndFreeNativeMemory(errorHandle));

                using var handler = new TransformedVectorDataSafeHandle(new IntPtr(output), outputCols, outputRows, DestroyTransformedDataNative);

                // Not looping through the outputRows because we know for now there is only 1 row. If that changes will need to update the code here.
                var outputArray = new double[outputCols.ToInt32()];

                for (int i = 0; i < outputCols.ToInt32(); i++)
                {
                    outputArray[i] = *output++;
                }

                var buffer = new VBuffer<double>(outputCols.ToInt32(), outputArray);
                return buffer;
            }

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_DestroyTransformedData"), SuppressUnmanagedCodeSecurity]
            private static unsafe extern bool DestroyTransformedDataNative(IntPtr columns, IntPtr rows, IntPtr items, out IntPtr errorHandle);

            private protected override bool CreateEstimatorHelper(out IntPtr estimator, out IntPtr errorHandle)
            {
                return CreateEstimatorNative(Options.WindowCalculation, Options.Horizon, Options.MaxWindowSize, Options.MinWindowSize, out estimator, out errorHandle);
            }

            private protected override bool CreateTransformerFromEstimatorHelper(TransformerEstimatorSafeHandle estimator, out IntPtr transformer, out IntPtr errorHandle) =>
                CreateTransformerFromEstimatorNative(estimator, out transformer, out errorHandle);

            private protected override bool DestroyEstimatorHelper(IntPtr estimator, out IntPtr errorHandle) =>
                DestroyEstimatorNative(estimator, out errorHandle);

            private protected override bool DestroyTransformerHelper(IntPtr transformer, out IntPtr errorHandle) =>
                DestroyTransformerNative(transformer, out errorHandle);

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_Fit", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool FitNative(TransformerEstimatorSafeHandle estimator, IntPtr grainsArray, IntPtr grainsArraySize, double value, out FitResult fitResult, out IntPtr errorHandle);
            private protected override bool FitHelper(TransformerEstimatorSafeHandle estimator, IntPtr grainsArray, IntPtr grainsArraySize, double value, out FitResult fitResult, out IntPtr errorHandle)
            {
                return FitNative(estimator, grainsArray, grainsArraySize, value, out fitResult, out errorHandle);

            }

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_CompleteTraining", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool CompleteTrainingNative(TransformerEstimatorSafeHandle estimator, out IntPtr errorHandle);
            private protected override bool CompleteTrainingHelper(TransformerEstimatorSafeHandle estimator, out IntPtr errorHandle) =>
                    CompleteTrainingNative(estimator, out errorHandle);

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_CreateTransformerSaveData", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool CreateTransformerSaveDataNative(TransformerEstimatorSafeHandle transformer, out IntPtr buffer, out IntPtr bufferSize, out IntPtr error);
            private protected override bool CreateTransformerSaveDataHelper(out IntPtr buffer, out IntPtr bufferSize, out IntPtr errorHandle) =>
                CreateTransformerSaveDataNative(TransformerHandle, out buffer, out bufferSize, out errorHandle);

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_GetState", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool GetStateNative(TransformerEstimatorSafeHandle estimator, out TrainingState trainingState, out IntPtr errorHandle);
            private protected override bool GetStateHelper(TransformerEstimatorSafeHandle estimator, out TrainingState trainingState, out IntPtr errorHandle) =>
                GetStateNative(estimator, out trainingState, out errorHandle);

            [DllImport("Featurizers", EntryPoint = "AnalyticalRollingWindowFeaturizer_double_OnDataCompleted", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool OnDataCompletedNative(TransformerEstimatorSafeHandle estimator, out IntPtr errorHandle);
            private protected override bool OnDataCompletedHelper(TransformerEstimatorSafeHandle estimator, out IntPtr errorHandle) =>
                    OnDataCompletedNative(estimator, out errorHandle);

            public override void Dispose()
            {
                if (!TransformerHandle.IsClosed)
                    TransformerHandle.Dispose();
            }
        }

        #endregion

        // On the native side, these rolling windows are implemented as 2 separate featurizers.
        // We are only exposing 1 interface in ML.Net, but there needs to be interop code for both.
        #region SimpleDoubleTypedColumn

        internal sealed class SimpleDoubleTypedColumn : TypedColumn<double, VBuffer<double>>
        {
            internal SimpleDoubleTypedColumn(string name, string source, RollingWindowEstimator.Options options, TransformerEstimatorSafeHandle transformer) :
                base(name, source, typeof(double).ToString(), options, transformer)
            {
            }

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_CreateEstimator", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool CreateEstimatorNative(RollingWindowEstimator.RollingWindowCalculation windowCalculation, UInt32 horizon, UInt32 maxWindowSize, UInt32 minWindowSize, out IntPtr estimator, out IntPtr errorHandle);

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_DestroyEstimator", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool DestroyEstimatorNative(IntPtr estimator, out IntPtr errorHandle); // Should ONLY be called by safe handle

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_CreateTransformerFromEstimator", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool CreateTransformerFromEstimatorNative(TransformerEstimatorSafeHandle estimator, out IntPtr transformer, out IntPtr errorHandle);

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_DestroyTransformer", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool DestroyTransformerNative(IntPtr transformer, out IntPtr errorHandle);
            internal override void CreateTransformerFromEstimator(IDataView input)
            {
                TransformerHandle = CreateTransformerFromEstimatorBase(input);
            }

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_CreateTransformerFromSavedData", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static unsafe extern bool CreateTransformerFromSavedDataNative(byte* rawData, IntPtr bufferSize, out IntPtr transformer, out IntPtr errorHandle);
            private protected override unsafe TransformerEstimatorSafeHandle CreateTransformerFromSavedDataHelper(byte* rawData, IntPtr dataSize)
            {
                var result = CreateTransformerFromSavedDataNative(rawData, dataSize, out IntPtr transformer, out IntPtr errorHandle);
                if (!result)
                    throw new Exception(GetErrorDetailsAndFreeNativeMemory(errorHandle));

                return new TransformerEstimatorSafeHandle(transformer, DestroyTransformerNative);
            }

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_Transform", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static unsafe extern bool TransformDataNative(TransformerEstimatorSafeHandle transformer, IntPtr grainsArray, IntPtr grainsArraySize, double value, out IntPtr outputCols, out IntPtr outputRows, out double* output, out IntPtr errorHandle);
            internal unsafe override VBuffer<double> Transform(IntPtr grainsArray, IntPtr grainsArraySize, double input)
            {
                var success = TransformDataNative(TransformerHandle, grainsArray, grainsArraySize, input, out IntPtr outputCols, out IntPtr outputRows, out double* output, out IntPtr errorHandle);
                if (!success)
                    throw new Exception(GetErrorDetailsAndFreeNativeMemory(errorHandle));

                using var handler = new TransformedVectorDataSafeHandle(new IntPtr(output), outputCols, outputRows, DestroyTransformedDataNative);

                var outputArray = new double[outputCols.ToInt32()];

                for (int i = 0; i < outputCols.ToInt32(); i++)
                {
                    outputArray[i] = *output++;
                }

                var buffer = new VBuffer<double>(outputCols.ToInt32(), outputArray);
                return buffer;
            }

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_DestroyTransformedData"), SuppressUnmanagedCodeSecurity]
            private static unsafe extern bool DestroyTransformedDataNative(IntPtr columns, IntPtr rows, IntPtr items, out IntPtr errorHandle);

            private protected override bool CreateEstimatorHelper(out IntPtr estimator, out IntPtr errorHandle)
            {
                // We are subtracting one from the window calculation because these are 2 different featurizers in the native code and both native enums
                // start at 1.
                return CreateEstimatorNative(Options.WindowCalculation - 1, Options.Horizon, Options.MaxWindowSize, Options.MinWindowSize, out estimator, out errorHandle);
            }

            private protected override bool CreateTransformerFromEstimatorHelper(TransformerEstimatorSafeHandle estimator, out IntPtr transformer, out IntPtr errorHandle) =>
                CreateTransformerFromEstimatorNative(estimator, out transformer, out errorHandle);

            private protected override bool DestroyEstimatorHelper(IntPtr estimator, out IntPtr errorHandle) =>
                DestroyEstimatorNative(estimator, out errorHandle);

            private protected override bool DestroyTransformerHelper(IntPtr transformer, out IntPtr errorHandle) =>
                DestroyTransformerNative(transformer, out errorHandle);

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_Fit", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool FitNative(TransformerEstimatorSafeHandle estimator, IntPtr grainsArray, IntPtr grainsArraySize, double value, out FitResult fitResult, out IntPtr errorHandle);
            private protected override bool FitHelper(TransformerEstimatorSafeHandle estimator, IntPtr grainsArray, IntPtr grainsArraySize, double value, out FitResult fitResult, out IntPtr errorHandle)
            {
                return FitNative(estimator, grainsArray, grainsArraySize, value, out fitResult, out errorHandle);

            }

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_CompleteTraining", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool CompleteTrainingNative(TransformerEstimatorSafeHandle estimator, out IntPtr errorHandle);
            private protected override bool CompleteTrainingHelper(TransformerEstimatorSafeHandle estimator, out IntPtr errorHandle) =>
                    CompleteTrainingNative(estimator, out errorHandle);

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_CreateTransformerSaveData", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool CreateTransformerSaveDataNative(TransformerEstimatorSafeHandle transformer, out IntPtr buffer, out IntPtr bufferSize, out IntPtr error);
            private protected override bool CreateTransformerSaveDataHelper(out IntPtr buffer, out IntPtr bufferSize, out IntPtr errorHandle) =>
                CreateTransformerSaveDataNative(TransformerHandle, out buffer, out bufferSize, out errorHandle);

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_GetState", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool GetStateNative(TransformerEstimatorSafeHandle estimator, out TrainingState trainingState, out IntPtr errorHandle);
            private protected override bool GetStateHelper(TransformerEstimatorSafeHandle estimator, out TrainingState trainingState, out IntPtr errorHandle) =>
                GetStateNative(estimator, out trainingState, out errorHandle);

            [DllImport("Featurizers", EntryPoint = "SimpleRollingWindowFeaturizer_double_OnDataCompleted", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
            private static extern bool OnDataCompletedNative(TransformerEstimatorSafeHandle estimator, out IntPtr errorHandle);
            private protected override bool OnDataCompletedHelper(TransformerEstimatorSafeHandle estimator, out IntPtr errorHandle) =>
                    OnDataCompletedNative(estimator, out errorHandle);

            public override void Dispose()
            {
                if (!TransformerHandle.IsClosed)
                    TransformerHandle.Dispose();
            }
        }

        #endregion

        #endregion

        #region IDataView

        internal sealed class RollingWindowDataView : ITransformCanSaveOnnx
        {
            private RollingWindowTransformer _parent;
            private readonly IDataView _source;
            private readonly IHostEnvironment _host;
            private readonly DataViewSchema _schema;
            private readonly RollingWindowEstimator.Options _options;

            internal RollingWindowDataView(IHostEnvironment env, IDataView input, RollingWindowEstimator.Options options, RollingWindowTransformer parent)
            {
                _host = env;
                _source = input;

                _options = options;
                _parent = parent;

                _schema = _parent.GetOutputSchema(input.Schema);
            }

            public bool CanShuffle => false;

            public DataViewSchema Schema => _schema;

            public IDataView Source => _source;

            public DataViewRowCursor GetRowCursor(IEnumerable<DataViewSchema.Column> columnsNeeded, Random rand = null)
            {
                _host.AssertValueOrNull(rand);

                return new Cursor(_host, _source, _parent.CloneTransformers(), _options, _schema);
            }

            // Can't use parallel cursors so this defaults to calling non-parallel version
            public DataViewRowCursor[] GetRowCursorSet(IEnumerable<DataViewSchema.Column> columnsNeeded, int n, Random rand = null) =>
                 new DataViewRowCursor[] { GetRowCursor(columnsNeeded, rand) };

            // We aren't changing the row count, so just get the _source row count
            public long? GetRowCount() => _source.GetRowCount();

            public void Save(ModelSaveContext ctx)
            {
                _parent.Save(ctx);
            }

            public void SaveAsOnnx(OnnxContext ctx)
            {
                _host.CheckValue(ctx, nameof(ctx));
                Contracts.Assert(CanSaveOnnx(ctx));

                string opType;

                // Since the native code exposes these as 2 different featurizers, we need to let ORT know which one it is.
                // 0 -> AnalyticalRollingWindow
                // 1,2 (the else branch) -> SimpleRollingWindow
                if (_options.WindowCalculation == RollingWindowEstimator.RollingWindowCalculation.Mean)
                    opType = "AnalyticalRollingWindowTransformer";
                else
                    opType = "SimpleRollingWindowTransformer";

                // Convert grain columns to strings
                CreateOnnxStringConversion(ctx, _parent._options.GrainColumns, out string[] grainStringColumns);

                // Combine all the grains into one tensor
                CreateOnnxColumnConcatenation(ctx, grainStringColumns, "grains", out string grainsTensorName);

                foreach (var column in _parent._columns)
                {
                    // srcVariable needs to have the "batch" removed
                    CreateSqueezeNode(ctx, ctx.GetVariableName(column.Source), NumberDataViewType.Double);

                    var srcVariableName = ctx.GetVariableName(column.Source);

                    if (!ctx.ContainsColumn(column.Source))
                        continue;

                    var dstVariableName = ctx.AddIntermediateVariable(new VectorDataViewType(NumberDataViewType.Double, 1, (int)_parent._options.Horizon), column.Name);

                    var state = column.CreateTransformerSaveData();
                    long[] dimensions = new long[] { state.Length };
                    var outputList = new List<string>() { dstVariableName };

                    var node = ctx.CreateNode(opType, new[] { ctx.AddInitializer(state, dimensions, "State"), grainsTensorName, srcVariableName },
                            outputList, ctx.GetNodeName(opType), "com.microsoft.mlfeaturizers");
                }
            }

            private void CreateOnnxStringConversion(OnnxContext ctx, string[] inputColumns, out string[] outputColumns)
            {
                // Create string "state" for the string featurizer for float and double type
                var state = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 };
                long[] dimensions = new long[] { state.Length };

                string opType = "StringTransformer";
                outputColumns = new string[inputColumns.Length];

                for (int i = 0; i < inputColumns.Length; i++)
                {
                    var baseType = _schema[inputColumns[i]].Type.RawType;
                    var srcVariableName = ctx.GetVariableName(inputColumns[i]);

                    // If we are already a string no need to convert.
                    if (baseType == typeof(ReadOnlyMemory<char>))
                    {
                        outputColumns[i] = srcVariableName;
                        continue;
                    }

                    var initializer = ctx.AddInitializer(state, dimensions, "ShortGrainStateInitializer");
                    var dstVariableName = ctx.AddIntermediateVariable(TextDataViewType.Instance, srcVariableName + "-stringoutput");
                    outputColumns[i] = dstVariableName;

                    ctx.CreateNode(opType, new[] { initializer, srcVariableName }, new[] { dstVariableName }, ctx.GetNodeName(opType), "com.microsoft.mlfeaturizers");
                }
            }

            private void CreateSqueezeNode(OnnxContext ctx, string columnName, DataViewType columnType)
            {
                string opType = "Squeeze";

                var column = ctx.GetVariableName(columnName);

                var dstVariableName = ctx.AddIntermediateVariable(columnType, columnName, true);

                var node = ctx.CreateNode(opType, column, dstVariableName, ctx.GetNodeName(opType), "");

                node.AddAttribute("axes", new long[] { 1 });

            }

            private void CreateOnnxColumnConcatenation(OnnxContext ctx, string[] inputColumns, string outputColumnPrefix, out string outputColumnName)
            {
                string opType = "Concat";
                outputColumnName = ctx.AddIntermediateVariable(TextDataViewType.Instance, outputColumnPrefix + "-concatstringsoutput", true);

                var node = ctx.CreateNode(opType, inputColumns, new[] { outputColumnName }, ctx.GetNodeName(opType), "");

                node.AddAttribute("axis", 1);
            }

            public bool CanSaveOnnx(OnnxContext ctx) => true;

            #region Cursor

            private sealed class Cursor : DataViewRowCursor
            {
                private static readonly FuncInstanceMethodInfo2<Cursor, DataViewRow, int, Delegate> _makeGetterMethodInfo
                    = FuncInstanceMethodInfo2<Cursor, DataViewRow, int, Delegate>.Create(target => target.MakeGetter<int, int>);

                private readonly IChannelProvider _ch;
                private IDataView _dataView;

                private DataViewRowCursor _sourceCursor;
                private long _position;
                private bool _sourceIsGood;
                private readonly DataViewSchema _schema;
                private TypedColumn[] _columns;
                private readonly RollingWindowEstimator.Options _options;
                private ValueGetter<ReadOnlyMemory<char>>[] _grainGetters;

                public Cursor(IChannelProvider provider, IDataView input, TransformerEstimatorSafeHandle[] transformers, RollingWindowEstimator.Options options, DataViewSchema schema)
                {
                    _ch = provider;
                    _ch.CheckValue(input, nameof(input));

                    _dataView = input;
                    _position = -1;
                    _schema = schema;
                    _options = options;
                    _grainGetters = new ValueGetter<ReadOnlyMemory<char>>[_options.GrainColumns.Length];

                    _sourceIsGood = true;

                    _sourceCursor = _dataView.GetRowCursorForAllColumns();

                    InitializeGrainGetters(_options.GrainColumns);

                    _columns = new TypedColumn[transformers.Length];
                    for (int i = 0; i < transformers.Length; i++)
                    {
                        _columns[i] = TypedColumn.CreateTypedColumn(options.Column[i].Name, options.Column[i].Source, input.Schema[options.Column[i].Source].Type.RawType.ToString(), options, transformers[i]);
                    }
                }

                public sealed override ValueGetter<DataViewRowId> GetIdGetter()
                {
                    return
                           (ref DataViewRowId val) =>
                           {
                               _ch.Check(_sourceIsGood, RowCursorUtils.FetchValueStateError);
                               val = new DataViewRowId((ulong)Position, 0);
                           };
                }

                public sealed override DataViewSchema Schema => _schema;

                public override bool IsColumnActive(DataViewSchema.Column column) => true;

                protected override void Dispose(bool disposing)
                {
                    foreach (var column in _columns)
                    {
                        column.Dispose();
                    }
                    _sourceCursor.Dispose();
                }

                /// <summary>
                /// Returns a value getter delegate to fetch the value of column with the given columnIndex, from the row.
                /// This throws if the column is not active in this row, or if the type.
                /// Since all we are doing is dropping rows, we can just use the source getter.
                /// <typeparamref name="TValue"/> differs from this column's type.
                /// </summary>
                /// <typeparam name="TValue"> is the column's content type.</typeparam>
                /// <param name="column"> is the output column whose getter should be returned.</param>
                public override ValueGetter<TValue> GetGetter<TValue>(DataViewSchema.Column column)
                {
                    _ch.Check(IsColumnActive(column));

                    if (_columns.Any(x => x.Name == column.Name && x.ReturnType().ToString() == column.Type.RawType.ToString()))
                    {
                        var index = Array.FindIndex(_columns, x => x.Name == column.Name);
                        Type inputType = _columns[index].SourceType();
                        Type outputType = _columns[index].ReturnType();

                        return (ValueGetter<TValue>)Utils.MarshalInvoke(_makeGetterMethodInfo, this, inputType, outputType, _sourceCursor, index);
                    }

                    return _sourceCursor.GetGetter<TValue>(column);
                }

                public override bool MoveNext()
                {
                    _position++;
                    _sourceIsGood = _sourceCursor.MoveNext();
                    return _sourceIsGood;
                }

                public sealed override long Position => _position;

                public sealed override long Batch => _sourceCursor.Batch;

                private void InitializeGrainGetters(string[] grainColumns)
                {
                    // Create getters for the source grain columns.

                    for (int i = 0; i < _grainGetters.Length; i++)
                    {
                        if (Schema[grainColumns[i]].Type.RawType == typeof(sbyte))
                            _grainGetters[i] = GetGrainGetter<sbyte>(grainColumns[i]);
                        else if (Schema[grainColumns[i]].Type.RawType == typeof(Int16))
                            _grainGetters[i] = GetGrainGetter<Int16>(grainColumns[i]);
                        else if (Schema[grainColumns[i]].Type.RawType == typeof(Int32))
                            _grainGetters[i] = GetGrainGetter<Int32>(grainColumns[i]);
                        else if (Schema[grainColumns[i]].Type.RawType == typeof(Int64))
                            _grainGetters[i] = GetGrainGetter<Int64>(grainColumns[i]);
                        else if (Schema[grainColumns[i]].Type.RawType == typeof(byte))
                            _grainGetters[i] = GetGrainGetter<byte>(grainColumns[i]);
                        else if (Schema[grainColumns[i]].Type.RawType == typeof(UInt16))
                            _grainGetters[i] = GetGrainGetter<UInt16>(grainColumns[i]);
                        else if (Schema[grainColumns[i]].Type.RawType == typeof(UInt32))
                            _grainGetters[i] = GetGrainGetter<UInt32>(grainColumns[i]);
                        else if (Schema[grainColumns[i]].Type.RawType == typeof(UInt64))
                            _grainGetters[i] = GetGrainGetter<UInt64>(grainColumns[i]);
                        else if (Schema[grainColumns[i]].Type.RawType == typeof(float))
                            _grainGetters[i] = GetGrainGetter<float>(grainColumns[i]);
                        else if (Schema[grainColumns[i]].Type.RawType == typeof(double))
                            _grainGetters[i] = GetGrainGetter<double>(grainColumns[i]);
                        else if (Schema[grainColumns[i]].Type.RawType == typeof(bool))
                            _grainGetters[i] = GetGrainGetter<bool>(grainColumns[i]);
                        if (Schema[grainColumns[i]].Type.RawType == typeof(ReadOnlyMemory<char>))
                            _grainGetters[i] = _sourceCursor.GetGetter<ReadOnlyMemory<char>>(Schema[grainColumns[i]]);
                    }
                }

                private ValueGetter<ReadOnlyMemory<char>> GetGrainGetter<T>(string grainColumn)
                {
                    var getter = _sourceCursor.GetGetter<T>(Schema[grainColumn]);
                    T value = default;
                    return (ref ReadOnlyMemory<char> dst) =>
                    {
                        getter(ref value);
                        dst = value.ToString().AsMemory();
                    };
                }

                private Delegate MakeGetter<TSourceType, TOutputType>(DataViewRow input, int iinfo)
                {
                    var inputColumn = input.Schema[_columns[iinfo].Source];
                    var srcGetterScalar = input.GetGetter<TSourceType>(inputColumn);

                    // Declaring these outside so they are only done once
                    GCHandle[] grainHandles = new GCHandle[_grainGetters.Length];
                    IntPtr[] grainArray = new IntPtr[grainHandles.Length];
                    GCHandle arrayHandle = default;

                    ValueGetter<TOutputType> result = (ref TOutputType dst) =>
                    {
                        TSourceType value = default;

                        // Build the string array
                        try
                        {
                            CreateGrainStringArrays(_grainGetters, ref grainHandles, ref arrayHandle, ref grainArray);

                            srcGetterScalar(ref value);

                            dst = ((TypedColumn<TSourceType, TOutputType>)_columns[iinfo]).Transform(arrayHandle.AddrOfPinnedObject(), new IntPtr(grainArray.Length), value);
                        }
                        finally
                        {
                            FreeGrainStringArrays(ref grainHandles, ref arrayHandle);
                        }
                    };

                    return result;
                }
            }

            #endregion Cursor

        }

        #endregion IDataView

    }

    internal static class RollingWindowEntrypoint
    {
        [TlcModule.EntryPoint(Name = "Transforms.RollingWindow",
            Desc = RollingWindowTransformer.Summary,
            UserName = RollingWindowTransformer.UserName,
            ShortName = RollingWindowTransformer.ShortName)]
        public static CommonOutputs.TransformOutput AnalyticalRollingWindow(IHostEnvironment env, RollingWindowEstimator.Options input)
        {
            var h = EntryPointUtils.CheckArgsAndCreateHost(env, RollingWindowTransformer.ShortName, input);
            var xf = new RollingWindowEstimator(h, input).Fit(input.Data).Transform(input.Data);
            return new CommonOutputs.TransformOutput()
            {
                Model = new TransformModelImpl(h, xf, input.Data),
                OutputData = xf
            };
        }
    }
}
