﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Encog.Util.Concurrency;
using Encog.Neural.Networks.Training;
using Encog.ML.Prg.Train;
using Encog.MathUtil.Randomize.Factory;
using Encog.ML.EA.Species;
using Encog.MathUtil.Randomize;
using Encog.ML.EA.Population;
using Encog.ML.EA.Exceptions;
using Encog.ML.Prg.Ext;
using Encog.ML.Prg.ExpValue;
using System.Threading.Tasks;
using Encog.ML.EA.Genome;

namespace Encog.ML.Prg.Generator
{
    /// <summary>
    /// The abstract base for Full and Grow program generation.
    /// </summary>
    public abstract class AbstractPrgGenerator : IPrgGenerator, IMultiThreadable
    {
        /// <summary>
        /// An optional scoring function.
        /// </summary>
        public ICalculateScore Score { get; set; }

        /// <summary>
        /// The program context to use.
        /// </summary>
        private EncogProgramContext context;

        /// <summary>
        /// The maximum depth to generate to.
        /// </summary>
        private int maxDepth;

        /// <summary>
        /// The minimum const to generate.
        /// </summary>
        public double MinConst { get; set; }

        /// <summary>
        /// The maximum const to generate.
        /// </summary>
        public double MaxConst { get; set; }

        /// <summary>
        /// True, if the program has enums.
        /// </summary>
        private bool hasEnum;

        /// <summary>
        /// The number of threads to use.
        /// </summary>
        public int ThreadCount { get; set; }

        /// <summary>
        /// The contents of this population, stored in rendered form. This prevents
        /// duplicates.
        /// </summary>
        private HashSet<String> contents = new HashSet<String>();

        /// <summary>
        /// A random number generator factory.
        /// </summary>
        public IRandomFactory RandomFactory { get; set; }

        /// <summary>
        /// The maximum number of allowed generation errors.
        /// </summary>
        public int MaxGenerationErrors { get; set; }

        /// <summary>
        /// Construct the generator.
        /// </summary>
        /// <param name="theContext">The context that is to be used for generation.</param>
        /// <param name="theMaxDepth">The maximum depth to generate to.</param>
        public AbstractPrgGenerator(EncogProgramContext theContext,
                int theMaxDepth)
        {
            if (theContext.Functions.Count == 0)
            {
                throw new EncogError("There are no opcodes defined");
            }

            this.context = theContext;
            this.maxDepth = theMaxDepth;
            this.hasEnum = this.context.HasEnum;
            Score = new ZeroEvalScoreFunction();
            MinConst = -10;
            MaxConst = 10;
            RandomFactory = EncogFramework.Instance.RandomFactory.FactorFactory();
            MaxGenerationErrors = 500;
        }

        /// <summary>
        /// Add a population member from one of the threads.
        /// </summary>
        /// <param name="population">The population to add to.</param>
        /// <param name="prg">The program to add.</param>
        public void AddPopulationMember(IPopulation population,
                EncogProgram prg)
        {
            lock (this)
            {
                ISpecies defaultSpecies = population.Species[0];
                prg.Species = defaultSpecies;
                defaultSpecies.Add(prg);
                this.contents.Add(prg.DumpAsCommonExpression());
            }
        }

        /// <summary>
        /// Attempt to create a genome. Cycle the specified number of times if an
        /// error occurs.
        /// </summary>
        /// <param name="rnd">The random number generator.</param>
        /// <param name="pop">The population.</param>
        /// <returns>The generated genome.</returns>
        public EncogProgram AttemptCreateGenome(EncogRandom rnd,
                IPopulation pop)
        {
            bool done = false;
            EncogProgram result = null;
            int tries = MaxGenerationErrors;

            while (!done)
            {
                result = (EncogProgram)Generate(rnd);
                result.Population = pop;

                double s;
                try
                {
                    tries--;
                    s = Score.CalculateScore(result);
                }
                catch (EARuntimeError e)
                {
                    s = double.NaN;
                }

                if (tries < 0)
                {
                    throw new EncogError("Could not generate a valid genome after "
                            + MaxGenerationErrors + " tries.");
                }
                else if (!Double.IsNaN(s) && !Double.IsInfinity(s)
                      && !this.contents.Contains(result.DumpAsCommonExpression()))
                {
                    done = true;
                }
            }

            return result;
        }

        /// <summary>
        /// Create a random note according to the specified paramaters.
        /// </summary>
        /// <param name="rnd">A random number generator.</param>
        /// <param name="program">The program to generate for.</param>
        /// <param name="depthRemaining">The depth remaining to generate.</param>
        /// <param name="types">The types to generate.</param>
        /// <param name="includeTerminal">Should we include terminal nodes.</param>
        /// <param name="includeFunction">Should we include function nodes.</param>
        /// <returns>The generated program node.</returns>
        public ProgramNode CreateRandomNode(EncogRandom rnd,
                EncogProgram program, int depthRemaining,
                IList<EPLValueType> types, bool includeTerminal,
                bool includeFunction)
        {

            // if we've hit the max depth, then create a terminal nodes, so it stops
            // here
            if (depthRemaining == 0)
            {
                return CreateTerminalNode(rnd, program, types);
            }

            // choose which opcode set we might create the node from
            IList<IProgramExtensionTemplate> opcodeSet = Context.Functions.FindOpcodes(types, Context,
                            includeTerminal, includeFunction);

            // choose a random opcode
            IProgramExtensionTemplate temp = GenerateRandomOpcode(rnd,
                    opcodeSet);
            if (temp == null)
            {
                throw new EACompileError(
                        "Trying to generate a random opcode when no opcodes exist.");
            }

            // create the child nodes
            int childNodeCount = temp.ChildNodeCount;
            ProgramNode[] children = new ProgramNode[childNodeCount];

            if (EncogOpcodeRegistry.IsOperator(temp.NodeType) && children.Length >= 2)
            {

                // for an operator of size 2 or greater make sure all children are
                // the same time
                IList<EPLValueType> childTypes = temp.Params[0]
                        .DetermineArgumentTypes(types);
                EPLValueType selectedType = childTypes[rnd
                        .Next(childTypes.Count)];
                childTypes.Clear();
                childTypes.Add(selectedType);

                // now create the children of a common type
                for (int i = 0; i < children.Length; i++)
                {
                    children[i] = CreateNode(rnd, program, depthRemaining - 1,
                            childTypes);
                }
            }
            else
            {

                // otherwise, let the children have their own types
                for (int i = 0; i < children.Length; i++)
                {
                    IList<EPLValueType> childTypes = temp.Params[i]
                            .DetermineArgumentTypes(types);
                    children[i] = CreateNode(rnd, program, depthRemaining - 1,
                            childTypes);
                }
            }

            // now actually create the node
            ProgramNode result = new ProgramNode(program, temp, children);
            temp.Randomize(rnd, types, result, MinConst, MaxConst);
            return result;
        }

        /// <summary>
        /// Create a terminal node.
        /// </summary>
        /// <param name="rnd">A random number generator.</param>
        /// <param name="program">The program to generate for.</param>
        /// <param name="types">The types that we might generate.</param>
        /// <returns>The terminal program node.</returns>
        public ProgramNode CreateTerminalNode(EncogRandom rnd,
                EncogProgram program, IList<EPLValueType> types)
        {
            IProgramExtensionTemplate temp = GenerateRandomOpcode(
                    rnd,
                    Context.Functions.FindOpcodes(types, this.context,
                            true, false));
            if (temp == null)
            {
                throw new EACompileError("No opcodes exist for the type: "
                        + types.ToString());
            }
            ProgramNode result = new ProgramNode(program, temp,
                    new ProgramNode[] { });

            temp.Randomize(rnd, types, result, MinConst, MaxConst);
            return result;
        }

        /// <summary>
        /// Determine the max depth.
        /// </summary>
        /// <param name="rnd">Random number generator.</param>
        /// <returns>The max depth.</returns>
        public virtual int DetermineMaxDepth(EncogRandom rnd)
        {
            return this.maxDepth;
        }

        /// <inheritdoc/>
        public IGenome Generate(EncogRandom rnd)
        {
            EncogProgram program = new EncogProgram(this.context);
            IList<EPLValueType> types = new List<EPLValueType>();
            types.Add(this.context.Result.VariableType);
            program.RootNode = CreateNode(rnd, program, DetermineMaxDepth(rnd),
                    types);
            return program;
        }

        /// <inheritdoc/>
        public void Generate(EncogRandom rnd, IPopulation pop)
        {
            // prepare population
            this.contents.Clear();
            pop.Species.Clear();
            ISpecies defaultSpecies = pop.CreateSpecies();
            
            if (this.Score.RequireSingleThreaded || ThreadCount == 1)
            {
                for (int i = 0; i < pop.PopulationSize; i++)
                {
                    EncogProgram prg = AttemptCreateGenome(rnd, pop);
                    AddPopulationMember(pop, prg);                    
                }
            }
            else
            {
                Parallel.For(0, pop.PopulationSize, (i) =>
                {
                    EncogProgram prg = AttemptCreateGenome(rnd, pop);
                    AddPopulationMember(pop, prg);
                });
            }

            // just pick a leader, for the default species.
            defaultSpecies.Leader = defaultSpecies.Members[0];
        }

        /// <summary>
        /// Generate a random opcode.
        /// </summary>
        /// <param name="rnd">Random number generator.</param>
        /// <param name="opcodes">The opcodes to choose from.</param>
        /// <returns>The selected opcode.</returns>
        public IProgramExtensionTemplate GenerateRandomOpcode(EncogRandom rnd,
                IList<IProgramExtensionTemplate> opcodes)
        {
            int maxOpCode = opcodes.Count;

            if (maxOpCode == 0)
            {
                return null;
            }

            int tries = 10000;

            IProgramExtensionTemplate result = null;

            while (result == null)
            {
                int opcode = rnd.Next(maxOpCode);
                result = opcodes[opcode];
                tries--;
                if (tries < 0)
                {
                    throw new EACompileError(
                            "Could not generate an opcode.  Make sure you have valid opcodes defined.");
                }
            }
            return result;
        }

        /// <summary>
        /// The context.
        /// </summary>
        public EncogProgramContext Context
        {
            get
            {
                return this.context;
            }
        }

        /// <summary>
        /// The max depth.
        /// </summary>
        public int MaxDepth
        {
            get
            {
                return this.maxDepth;
            }
        }

        /// <summary>
        /// Do we have enums?
        /// </summary>
        public bool HasEnum
        {
            get
            {
                return this.hasEnum;
            }
        }

        /// <inheritdoc/>
        public abstract ProgramNode CreateNode(EncogRandom rnd, EncogProgram program, int depthRemaining, IList<EPLValueType> types);
    }
}