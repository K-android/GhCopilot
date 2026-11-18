# GH Copilot Golden Prompts & Evaluation Set (Section 8)

All runs append automatically to `%LOCALAPPDATA%\GhCopilot\runs.jsonl` (`RunLogEntry`) and `%LOCALAPPDATA%\GhCopilot\sent-payloads.jsonl` (`PayloadAuditEntry`).

## Target Metrics for v1
- `% valid on first pass` (`ValidFirstPass` / total runs)
- `% valid after repair` (`ValidFirstPass + ValidAfterRepair + RuntimeRepairValid` / total runs — **target >= 80%**)
- `% solving with zero runtime errors`
- `% accepted` (user clicked Accept Preview)
- Median latency and token usage

---

## Golden Prompts (25 Prompts)

### A. Simple Math & Sliders (5)
1. `add two sliders 12.5 and 7.5 and show the sum in a panel`
2. `multiply a radius slider by 2*pi to calculate circumference`
3. `remap a slider from 0..10 to 100..500`
4. `create a numeric expression x^2 + 2*x + 1 driven by a slider`
5. `check if an input number is greater than 25 and output boolean`

### B. Lists, Series & Data Trees (5)
6. `create a series of 10 numbers starting at 0 with step 2.5`
7. `generate 20 random points inside a 10x10 rectangle`
8. `cull every second item from a 10-element range`
9. `sort a list of random numbers and extract the largest 3`
10. `create a 5x5 grid of points using cross-referenced X and Y ranges`

### C. Geometry & Surfaces (5)
11. `circle with a radius slider`
12. `extrude a 10x5 rectangle vertically by a height slider`
13. `divide a circle into 12 equal points and connect them with a polyline`
14. `create a lofted surface through 3 concentric circles at Z = 0, 5, 10`
15. `make a spiral staircase` *(expected Python 3 script fallback)*

### D. Company Library & Clusters (5)
16. `subdivide a surface using our company facade panelizer`
17. `apply company standard mullion profile to boundary curves`
18. `tag panel areas with the company annotation user object`
19. `create a structural truss grid using the company truss cluster`
20. `run company floor area ratio check on closed boundary curves`

### E. Canvas Awareness — Explain & Fix Selection (5)
21. `Explain selected circle definition with a zero-radius slider`
22. `Fix selected Circle component failing because radius is 0`
23. `Fix selected Divide Curve where count slider is set to 0`
24. `Fix selected Addition component wired to text instead of numbers`
25. `Explain why selected Extrude component shows a runtime warning`
