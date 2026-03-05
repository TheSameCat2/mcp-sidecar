#include <iostream>
#include <string>

void greet(const std::string& name) {
    std::cout << "Hello, " << name << "!" << std::endl;
}

int add(int a, int b) {
    return a + b;
}

int main() {
    greet("World");
    int result = add(2, 3);
    std::cout << "2 + 3 = " << result << std::endl;
    return 0;
}
