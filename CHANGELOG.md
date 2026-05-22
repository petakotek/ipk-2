# Changelog

## Známe limitace řešení

Retransmit logika je implementována za pomocí pevně definovaného časového intervalu, v případě, kdy doba příjmu paketu k jedné či druhé straně je přímo úměrná nebo dokonce vyšší, může program zbytečně spadnout. Řešením by byla implementace výpočtu tohoto intervalu například za pomocí RTT, řešení však z důvodu časové tísně tuto implementaci nemá, jak již bylo zmíněno v sekci Strategie znovuodesílání paketů a zpracování limitu času.

## [0.3.0] - 2026-05-02
### Přidáno
- Vylepšení implementace 
- Přidány automatické testy pro kontrolu pomocných metod 
- zvětšena velikost okna
- zveřejnění dokumentace a Changelog souboru

## [0.2.2] - 2026-05-01

### Přidáno
- Přidána vylepšená logika aplikace, vyřešení ověřování spojení mezi klientem a serverem.
- Přidány testy pro strukturu paketu
- Příprava dokumentace a přidání prvotních obrázků
- Implementace retransmit logiky aplikace

## [0.2.1] - 2026-04-30

### Přidáno

- Přidána základní implementace logiky aplikace - komunikace mezi klientem a serverem

## [0.1.1] - 2026-03-29

### Přidáno

- Implementace základní logiky pro zkracování argumentů příkazové řádky. Implementace základní logiky nastavování soketů pro odesílání na stranu přijemce.
- Přidán Makefile
- Přidán .gitignore pro C#

## [0.1.0] - 2026-04-13

### Přidáno

- Incializace prostředí, vytvoření Makefile a nastavení