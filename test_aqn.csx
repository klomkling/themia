using System;

// Test 1: Normal enum
var enumType = typeof(DayOfWeek);
Console.WriteLine($"1. DayOfWeek AQN: {enumType.AssemblyQualifiedName}");
Console.WriteLine($"   DayOfWeek IsGenericTypeDefinition: {enumType.IsGenericTypeDefinition}");

// Test 2: Open generic type
var openGeneric = typeof(List<>);
Console.WriteLine($"\n2. List<> AQN: {openGeneric.AssemblyQualifiedName}");
Console.WriteLine($"   List<> IsGenericTypeDefinition: {openGeneric.IsGenericTypeDefinition}");

// Test 3: Constructed generic type
var constructedGeneric = typeof(List<int>);
Console.WriteLine($"\n3. List<int> AQN: {constructedGeneric.AssemblyQualifiedName}");
Console.WriteLine($"   List<int> IsGenericTypeDefinition: {constructedGeneric.IsGenericTypeDefinition}");

// Test 4: Can enums be generic?
Console.WriteLine($"\n4. Can enums be generic? NO - C# does not support generic enum types.");
Console.WriteLine($"   The compiler forbids: enum MyEnum<T> {{ ... }} ");
