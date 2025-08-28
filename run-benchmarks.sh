#!/bin/bash

# Bash script to run Respire benchmarks locally

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Default values
BENCHMARK_TYPE="All"
FRAMEWORK="Both"
FILTER="*"
OPEN_RESULTS=false

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -t|--type)
            BENCHMARK_TYPE="$2"
            shift 2
            ;;
        -f|--framework)
            FRAMEWORK="$2"
            shift 2
            ;;
        --filter)
            FILTER="$2"
            shift 2
            ;;
        -o|--open)
            OPEN_RESULTS=true
            shift
            ;;
        -h|--help)
            echo "Usage: $0 [options]"
            echo "Options:"
            echo "  -t, --type <Container|Throughput|All>  Benchmark type to run (default: All)"
            echo "  -f, --framework <net8.0|net9.0|Both>   Framework to test (default: Both)"
            echo "  --filter <pattern>                      Custom filter pattern"
            echo "  -o, --open                              Open results in browser"
            echo "  -h, --help                              Show this help message"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

echo -e "${CYAN}Respire Benchmark Runner${NC}"
echo -e "${CYAN}=====================${NC}"

# Build the solution first
echo -e "\n${YELLOW}Building solution in Release mode...${NC}"
dotnet build -c Release

if [ $? -ne 0 ]; then
    echo -e "${RED}Build failed! Please fix build errors before running benchmarks.${NC}"
    exit 1
fi

# Navigate to benchmark project
cd benchmarks/Respire.Benchmarks || exit 1

# Determine frameworks to test
if [ "$FRAMEWORK" = "Both" ]; then
    FRAMEWORKS=("net8.0" "net9.0")
else
    FRAMEWORKS=("$FRAMEWORK")
fi

# Run benchmarks for each framework
for fw in "${FRAMEWORKS[@]}"; do
    echo -e "\n${GREEN}Running benchmarks for $fw...${NC}"
    
    # Determine benchmark filter
    case $BENCHMARK_TYPE in
        Container)
            BENCHMARK_FILTER="*RedisContainerBenchmarks*"
            ;;
        Throughput)
            BENCHMARK_FILTER="*RedisThroughputBenchmarks*"
            ;;
        All)
            BENCHMARK_FILTER="*"
            ;;
        *)
            echo -e "${RED}Invalid benchmark type: $BENCHMARK_TYPE${NC}"
            exit 1
            ;;
    esac
    
    # Apply custom filter if provided
    if [ "$FILTER" != "*" ]; then
        BENCHMARK_FILTER="$FILTER"
    fi
    
    echo -e "Filter: ${BENCHMARK_FILTER}"
    
    # Run benchmarks
    dotnet run -c Release -f "$fw" -- \
        --filter "$BENCHMARK_FILTER" \
        --exporters json markdown html \
        --artifacts "./BenchmarkDotNet.Artifacts/$fw"
    
    if [ $? -ne 0 ]; then
        echo -e "${RED}Benchmark run failed for $fw!${NC}"
    else
        echo -e "${GREEN}Benchmarks completed successfully for $fw${NC}"
        
        RESULTS_PATH="./BenchmarkDotNet.Artifacts/$fw/results"
        if [ -d "$RESULTS_PATH" ]; then
            echo -e "${CYAN}Results saved to: $RESULTS_PATH${NC}"
            
            # Open results if requested
            if [ "$OPEN_RESULTS" = true ]; then
                HTML_FILE=$(find "$RESULTS_PATH" -name "*.html" | head -n 1)
                if [ -n "$HTML_FILE" ]; then
                    echo -e "${YELLOW}Opening results in browser...${NC}"
                    if command -v xdg-open &> /dev/null; then
                        xdg-open "$HTML_FILE"
                    elif command -v open &> /dev/null; then
                        open "$HTML_FILE"
                    else
                        echo -e "${YELLOW}Please open manually: $HTML_FILE${NC}"
                    fi
                fi
            fi
        fi
    fi
done

echo -e "\n${CYAN}==============================${NC}"
echo -e "${GREEN}All benchmarks completed!${NC}"
echo -e "${CYAN}==============================${NC}"

cat << EOF

Benchmark Tips:
- For quick validation: ./run-benchmarks.sh -t Container -f net9.0
- For full comparison: ./run-benchmarks.sh -t All -f Both
- To open results automatically: ./run-benchmarks.sh -o
- Custom filter: ./run-benchmarks.sh --filter "*Set*"

Results are saved in: benchmarks/Respire.Benchmarks/BenchmarkDotNet.Artifacts/
EOF