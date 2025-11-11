# Changelog

All notable changes to this project will be documented in this file.

The format follows a simple Keep a Changelog style with ISO 8601 dates.

## [Unreleased] - 2025-10-25

### Added
- Full CIP-40 collateral support (Babbage/Conway) in the transaction builder:
  - Wires CollateralReturn (key 16) and TotalCollateral (key 17) when applicable.
  - Auto-tags eligible spending input(s) as collateral when none are provided (vkey-controlled only).
  - New builder APIs:
    - SetCollateralInputs(List<TransactionInput> inputs)
    - SetCollateralReturnAddress(Address address)
    - (existing) SetCollateralReturn(...), SetTotalCollateral(...), AddCollateral(...)
- Alonzo collateral handling (legacy): include Collateral (key 13) only; enforce ADA-only collateral.
- Era-aware collateral/fee loop:
  - Computes required collateral from fee and protocol params (CollateralPercentage).
  - Respects MaxCollateralInputs.
  - Builds CollateralReturn value and enforces min-ADA for the return output when tokens are present.
  - Excludes collateral UTxOs from spendable balance and change computation.
- Address-type validation for collateral: collateral inputs must be payment key-hash addresses (no script credentials).
- Tests for collateral auto-tagging and CBOR round-trip of collateral fields.

### Changed
- Fee calculation integrates collateral selection so fee reflects increased body size when collateral is present.

### Notes for integrators
- For Plutus transactions, collateral will be added automatically when not explicitly set. Use SetCollateralInputs(...) to override, and SetCollateralReturnAddress(...) to direct the collateral return output (Babbage/Conway).
- In Alonzo era transactions, ensure the chosen collateral UTxOs are ADA-only; token-bearing collateral requires CIP-40 (Babbage/Conway).
